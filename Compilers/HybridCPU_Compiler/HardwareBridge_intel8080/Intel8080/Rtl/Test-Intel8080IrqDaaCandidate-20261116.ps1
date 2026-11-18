param([Parameter(Mandatory=$true)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
$base=[IO.Path]::GetFullPath('C:\Users\Yuriy\Desktop\CircuitWeave-main\artifacts\8080').TrimEnd('\')+'\'
$out=[IO.Path]::GetFullPath($OutputDirectory)
if(-not $out.StartsWith($base,[StringComparison]::OrdinalIgnoreCase)){throw 'Output must be inside artifacts\8080'}
for($d=[IO.DirectoryInfo]::new($out);$null -ne $d;$d=$d.Parent){if((Test-Path -LiteralPath $d.FullName) -and (($d.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)){throw "Linked path refused: $($d.FullName)"}}
if(Test-Path -LiteralPath $out){throw 'Output directory already exists'}
[void][IO.Directory]::CreateDirectory($out)
$src=Join-Path $base 'audit-20261116-codex-01'
$rtl=Join-Path $src 'cw8080_subset_cpu_irq_daa_candidate.sv'
$log=Join-Path $out 'simulation.log'
$iv=Get-Command iverilog -ErrorAction SilentlyContinue; $vvp=Get-Command vvp -ErrorAction SilentlyContinue
if(!$iv -or !$vvp){[IO.File]::WriteAllText($log,"NOT RUN: iverilog and/or vvp unavailable`n"); Write-Output "NOT RUN: $log"; exit 2}
$lines=[Collections.Generic.List[string]]::new()
foreach($entry in @(@('tb_cw8080_daa_cpu','tb_cw8080_daa_irq_revision.sv'),@('tb_irq_held_halt','tb_irq_held_halt.sv'))){
  $bench=Join-Path $src $entry[1]; $image=Join-Path $out ($entry[0]+'.vvp')
  foreach($file in @($rtl,$bench)){$lines.Add("SHA256 $((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash) $file")}
  $result=& $iv.Source -g2012 -s $entry[0] -o $image $rtl $bench 2>&1; $code=$LASTEXITCODE; foreach($x in $result){$lines.Add("$x")}; if($code -ne 0){$lines.Add("COMPILE FAIL $code");[IO.File]::WriteAllLines($log,$lines);exit $code}
  $result=& $vvp.Source $image 2>&1; $code=$LASTEXITCODE; foreach($x in $result){$lines.Add("$x")}; if($code -ne 0){$lines.Add("SIMULATION FAIL $code");[IO.File]::WriteAllLines($log,$lines);exit $code}
}
$lines.Add('PASS: two directed benches; no full CPU validation claimed'); [IO.File]::WriteAllLines($log,$lines); Write-Output "PASS: $log"