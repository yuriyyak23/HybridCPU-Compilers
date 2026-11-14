param([string]$SourceRoot='C:\Users\Yuriy\Desktop\8080-emulator-net-master\emu8080.Core')
$ErrorActionPreference='Stop'
$project=Join-Path $PSScriptRoot 'HardwareBridge.Intel8080.csproj'
$artifactsRoot=[IO.Path]::GetFullPath('C:\Users\Yuriy\Desktop\CircuitWeave-main\artifacts\8080')
for($d=[IO.DirectoryInfo]::new($artifactsRoot);$null -ne $d;$d=$d.Parent){if((Test-Path -LiteralPath $d.FullName) -and (($d.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)){throw "Linked artifact path refused: $($d.FullName)"}}
if(-not(Test-Path -LiteralPath $artifactsRoot -PathType Container)){[void][IO.Directory]::CreateDirectory($artifactsRoot)}
$temp=Join-Path $artifactsRoot ('test-hb8080-ir-'+[Guid]::NewGuid().ToString('N'))
if(Test-Path -LiteralPath $temp){throw "Refusing existing test directory: $temp"}
[void][IO.Directory]::CreateDirectory($temp)
try {
  $names=@('Bus.cs','Cpu.cs','Flags.cs','Memory.cs','Ops.cs','Registers.cs','Utils.cs')
  $paths=@($names|ForEach-Object{$p=Join-Path $SourceRoot $_;if(!(Test-Path -LiteralPath $p -PathType Leaf)){throw "Missing source $p"};[IO.Path]::GetFullPath($p)})
  $before=@($paths|ForEach-Object{(Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash})
  $selection=Join-Path $temp 'selected.json'; $output=Join-Path $temp 'inventory.json'
  [IO.File]::WriteAllText($selection,($paths|ConvertTo-Json -Compress),[Text.UTF8Encoding]::new($false))
  & dotnet run --project $project -- $SourceRoot $selection $output
  if($LASTEXITCODE -ne 0){throw "Analyzer failed with exit $LASTEXITCODE"}
  $inventory=Get-Content -LiteralPath $output -Raw|ConvertFrom-Json
  $ir=Get-Content -LiteralPath (Join-Path $temp 'opcode-ir-candidate.json') -Raw|ConvertFrom-Json
  if($inventory.files.Count -ne 7 -or $inventory.types.Count -ne 7){throw 'Expected seven imported files and types.'}
  if($inventory.opcodes.Count -ne 166 -or $ir.entries.Count -ne 166){throw 'Expected 166 source dispatch and IR entries.'}
  if(@($inventory.opcodes.code|Sort-Object -Unique).Count -ne 166){throw 'Duplicate dispatch opcode found.'}
  if(@($ir.entries|Where-Object{$_.bindingStatus -ne 'Roslyn symbol resolved' -or [IO.Path]::GetFileName($_.sourceFile) -ne 'Ops.cs' -or $_.sourceLine -lt 1}).Count -ne 0){throw 'Unresolved method or missing Ops.cs source location.'}
  if(@($ir.entries|Where-Object{$_.hardwareStatus -notlike 'TODO:*' -or $_.manualTasks.Count -lt 3}).Count -ne 0){throw 'IR candidate overstates hardware readiness or omits manual tasks.'}
  $dependencyKinds=@($inventory.memberDependencies|Select-Object -ExpandProperty kind -Unique)
  foreach($kind in @('method-call','object-creation','field-reference','property-reference','event-reference')){
    if($kind -notin $dependencyKinds){throw "Expected semantic dependency kind missing: $kind"}
  }
  if(@($inventory.memberDependencies|Where-Object{$_.sourceMember -notmatch '^\w+\.' -or $_.line -lt 1}).Count -ne 0){throw 'Member dependency is missing its owning source member or line.'}
  if(@($inventory.memberDependencies|Where-Object{$_.targetFile -and $_.targetLine -lt 1}).Count -ne 0){throw 'Internal dependency target has an invalid source line.'}
  $after=@($paths|ForEach-Object{(Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash})
  if(($before -join ',') -ne ($after -join ',')){throw 'Input sources changed during analysis.'}
  Write-Output "PASS: 7 files/types, 166 unique dispatch entries, 166 Roslyn method bindings, $($inventory.memberDependencies.Count) source-mapped member dependency occurrences, explicit manual hardware TODOs; input hashes unchanged."
}
finally {
  $resolved=[IO.Path]::GetFullPath($temp)
  if(-not $resolved.StartsWith($artifactsRoot.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)){throw "Unsafe test cleanup path: $resolved"}
  if(Test-Path -LiteralPath $resolved){if(((Get-Item -LiteralPath $resolved -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0){throw "Refusing linked test cleanup path: $resolved"};Remove-Item -LiteralPath $resolved -Recurse -Force}
}
