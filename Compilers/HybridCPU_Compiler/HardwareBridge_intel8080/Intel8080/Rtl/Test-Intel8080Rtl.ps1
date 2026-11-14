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
$generated=@($log,(Join-Path $out 'tb_cw8080_add.vvp'),(Join-Path $out 'tb_cw8080_subset_cpu.vvp'))
foreach($path in $generated){if(Test-Path -LiteralPath $path){throw "Refusing to overwrite existing test output: $path"}}
$sources=@('cw8080_add.sv','cw8080_subset_cpu.sv','tb_cw8080_add.sv','tb_cw8080_subset_cpu.sv')|ForEach-Object{Join-Path $rtlRoot $_}
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
  $lines.Add('STATUS PASS - both testbenches compiled and executed.')
} else {$lines.Add('STATUS NOT RUN - this script currently supports iverilog/vvp only.');[IO.File]::WriteAllLines($log,$lines,[Text.UTF8Encoding]::new($false));Write-Output "NOT RUN; see $log";exit 2}
[IO.File]::WriteAllLines($log,$lines,[Text.UTF8Encoding]::new($false));Write-Output "PASS; see $log"
