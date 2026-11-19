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
  $add=Get-Content -LiteralPath (Join-Path $temp 'add-behavior-projection.json') -Raw|ConvertFrom-Json
  $adc=Get-Content -LiteralPath (Join-Path $temp 'adc-behavior-projection.json') -Raw|ConvertFrom-Json
  $sbb=Get-Content -LiteralPath (Join-Path $temp 'sbb-behavior-projection.json') -Raw|ConvertFrom-Json
  $sub=Get-Content -LiteralPath (Join-Path $temp 'sub-behavior-projection.json') -Raw|ConvertFrom-Json
  $dad=Get-Content -LiteralPath (Join-Path $temp 'dad-behavior-projection.json') -Raw|ConvertFrom-Json
  $logical=Get-Content -LiteralPath (Join-Path $temp 'logical-behavior-projection.json') -Raw|ConvertFrom-Json
  if($inventory.files.Count -ne 7 -or $inventory.types.Count -ne 7){throw 'Expected seven imported files and types.'}
  if($inventory.opcodes.Count -ne 166 -or $ir.entries.Count -ne 166){throw 'Expected 166 source dispatch and IR entries.'}
  if(@($inventory.opcodes.code|Sort-Object -Unique).Count -ne 166){throw 'Duplicate dispatch opcode found.'}
  if(@($ir.entries|Where-Object{$_.bindingStatus -ne 'Roslyn symbol resolved' -or [IO.Path]::GetFileName($_.sourceFile) -ne 'Ops.cs' -or $_.sourceLine -lt 1}).Count -ne 0){throw 'Unresolved method or missing Ops.cs source location.'}
  if(@($ir.entries|Where-Object{$_.hardwareStatus -notlike 'TODO:*' -or $_.manualTasks.Count -lt 3}).Count -ne 0){throw 'IR candidate overstates hardware readiness or omits manual tasks.'}
  if($add.schema -ne 'hardwarebridge.intel8080.add-hwir/v1' -or $add.dispatch.Count -ne 4 -or $add.equations.Count -ne 9){throw 'Source-backed ADD projection has an unexpected schema or coverage.'}
  if($add.disposition -notlike 'source-snapshot-pinned*'){throw 'ADD projection does not declare its pinned source-snapshot check.'}
  if(@($add.sourceHashes|Where-Object{$_.Sha256 -notmatch '^[0-9a-f]{64}$'}).Count -ne 3){throw 'ADD projection must carry three pinned source hashes.'}
  if((@($add.dispatch|ForEach-Object{$_.opcode}) -join ',') -ne '0x80,0x81,0x82,0x87'){throw 'ADD source dispatch coverage changed.'}
  if(@($add.equations|Where-Object{$_.name -in @('sum9','result8','A_next','CY_next','AC_next','Z_next','S_next','P_next','PC_next')}).Count -ne 9){throw 'ADD equation set is incomplete.'}
  $badAddTrace=@($add.sourceTrace|Where-Object{$_.sha256 -notmatch '^[0-9a-f]{64}$' -or $_.startLine -lt 1 -or $_.endLine -lt $_.startLine})
  if(@($add.sourceTrace).Count -ne 8 -or $badAddTrace.Count -ne 0){throw "ADD source trace is incomplete or malformed: count=$(@($add.sourceTrace).Count), invalid=$($badAddTrace.Count), details=$($badAddTrace|ConvertTo-Json -Compress)"}
  if($adc.schema -ne 'hardwarebridge.intel8080.adc-hwir/v1' -or $adc.dispatch.Count -ne 6 -or $adc.equations.Count -ne 9){throw 'Source-backed ADC projection has an unexpected schema or coverage.'}
  if($adc.disposition -notlike 'source-snapshot-pinned*' -or @($adc.sourceHashes|Where-Object{$_.Sha256 -notmatch '^[0-9a-f]{64}$'}).Count -ne 3){throw 'ADC projection must declare and carry three pinned source hashes.'}
  if((@($adc.dispatch|ForEach-Object{$_.opcode}) -join ',') -ne '0x88,0x89,0x8B,0x8C,0x8D,0x8E'){throw 'ADC source dispatch coverage changed.'}
  if(@($adc.equations|Where-Object{$_.name -in @('sum9','result8','A_next','CY_next','AC_next','Z_next','S_next','P_next','PC_next')}).Count -ne 9){throw 'ADC equation set is incomplete.'}
  $badAdcTrace=@($adc.sourceTrace|Where-Object{$_.sha256 -notmatch '^[0-9a-f]{64}$' -or $_.startLine -lt 1 -or $_.endLine -lt $_.startLine})
  if(@($adc.sourceTrace).Count -ne 14 -or $badAdcTrace.Count -ne 0){throw "ADC source trace is incomplete or malformed: count=$(@($adc.sourceTrace).Count), invalid=$($badAdcTrace.Count)"}
  if($sbb.schema -ne 'hardwarebridge.intel8080.sbb-hwir/v1' -or $sbb.dispatch.Count -ne 7 -or $sbb.equations.Count -ne 9){throw 'Source-backed SBB projection has an unexpected schema or coverage.'}
  if($sbb.disposition -notlike 'source-snapshot-pinned*' -or @($sbb.sourceHashes|Where-Object{$_.Sha256 -notmatch '^[0-9a-f]{64}$'}).Count -ne 3){throw 'SBB projection must declare and carry three pinned source hashes.'}
  if((@($sbb.dispatch|ForEach-Object{$_.opcode}) -join ',') -ne '0x99,0x9A,0x9B,0x9C,0x9D,0x9E,0x9F'){throw 'SBB source dispatch coverage changed.'}
  if(@($sbb.equations|Where-Object{$_.name -in @('result16','result8','A_next','CY_next','AC_source_next','Z_next','S_next','P_next','PC_next')}).Count -ne 9){throw 'SBB source equation set is incomplete.'}
  if($sbb.architectureSelection.auxCarryName -ne 'AC_spec_next' -or $sbb.architectureSelection.formula -notlike '*CY_in*'){throw 'SBB architecture-selected AC equation is missing or conflated with source AC.'}
  $badSbbTrace=@($sbb.sourceTrace|Where-Object{$_.sha256 -notmatch '^[0-9a-f]{64}$' -or $_.startLine -lt 1 -or $_.endLine -lt $_.startLine})
  if(@($sbb.sourceTrace).Count -ne 15 -or $badSbbTrace.Count -ne 0){throw "SBB source trace is incomplete or malformed: count=$(@($sbb.sourceTrace).Count), invalid=$($badSbbTrace.Count)"}
  if($sub.schema -ne 'hardwarebridge.intel8080.sub-hwir/v1' -or $sub.dispatch.Count -ne 2 -or $sub.equations.Count -ne 11){throw 'Source-backed SUB projection has an unexpected schema or coverage.'}
  if($sub.disposition -notlike 'source-snapshot-pinned*' -or @($sub.sourceHashes|Where-Object{$_.Sha256 -notmatch '^[0-9a-f]{64}$'}).Count -ne 3){throw 'SUB projection must declare and carry three pinned source hashes.'}
  if((@($sub.dispatch|ForEach-Object{$_.opcode}) -join ',') -ne '0x90,0xD6'){throw 'SUB source dispatch coverage changed.'}
  if(@($sub.equations|Where-Object{$_.name -in @('result16','result8','A_next','CY_next','AC_SUB_B_source_next','AC_SUI_source_effect','Z_next','S_next','P_next','PC_SUB_B_next','PC_SUI_next')}).Count -ne 11){throw 'SUB source equation/effect set is incomplete.'}
  if($sub.architectureSelection.auxCarryName -ne 'AC_spec_next' -or $sub.architectureSelection.formula -notlike '*operand*'){throw 'SUB architecture-selected AC equation is missing.'}
  $badSubTrace=@($sub.sourceTrace|Where-Object{$_.sha256 -notmatch '^[0-9a-f]{64}$' -or $_.startLine -lt 1 -or $_.endLine -lt $_.startLine})
  if(@($sub.sourceTrace).Count -ne 10 -or $badSubTrace.Count -ne 0){throw "SUB source trace is incomplete or malformed: count=$(@($sub.sourceTrace).Count), invalid=$($badSubTrace.Count)"}
  if($dad.schema -ne 'hardwarebridge.intel8080.dad-hwir/v1' -or $dad.dispatch.Count -ne 4 -or $dad.equations.Count -ne 8){throw 'Source-backed DAD projection has an unexpected schema or coverage.'}
  if($dad.disposition -notlike 'source-snapshot-pinned*' -or @($dad.sourceHashes|Where-Object{$_.Sha256 -notmatch '^[0-9a-f]{64}$'}).Count -ne 4){throw 'DAD projection must declare and carry four pinned source hashes.'}
  if((@($dad.dispatch|ForEach-Object{$_.opcode}) -join ',') -ne '0x09,0x19,0x29,0x39'){throw 'DAD source dispatch coverage changed.'}
  if(@($dad.equations|Where-Object{$_.name -in @('sum17','HL_next','CY_next','S_next','Z_next','P_next','AC_next','PC_next')}).Count -ne 8){throw 'DAD source equation/effect set is incomplete.'}
  $badDadTrace=@($dad.sourceTrace|Where-Object{$_.sha256 -notmatch '^[0-9a-f]{64}$' -or $_.startLine -lt 1 -or $_.endLine -lt $_.startLine})
  if(@($dad.sourceTrace).Count -ne 6 -or $badDadTrace.Count -ne 0){throw "DAD source trace is incomplete or malformed: count=$(@($dad.sourceTrace).Count), invalid=$($badDadTrace.Count)"}
  if($logical.schema -ne 'hardwarebridge.intel8080.logical-hwir/v1' -or $logical.dispatch.Count -ne 9 -or $logical.equations.Count -ne 20){throw 'Source-backed logical projection has an unexpected schema or coverage.'}
  if($logical.disposition -notlike 'source-snapshot-pinned*' -or @($logical.sourceHashes|Where-Object{$_.Sha256 -notmatch '^[0-9a-f]{64}$'}).Count -ne 3){throw 'Logical projection must declare and carry three pinned source hashes.'}
  if((@($logical.dispatch|ForEach-Object{$_.opcode}) -join ',') -ne '0xA0,0xA7,0xAA,0xAF,0xB0,0xB6,0xE6,0xEE,0xF6'){throw 'Logical source dispatch coverage changed.'}
  if($logical.architectureSelection.ORI_operation -ne 'A | data' -or @($logical.equations|Where-Object{$_.name -eq 'ORI_source_result' -and $_.formula -like '*A & data*'}).Count -ne 1){throw 'Logical projection must preserve source AOR behavior separately from architectural ORI.'}
  $badLogicalTrace=@($logical.sourceTrace|Where-Object{$_.sha256 -notmatch '^[0-9a-f]{64}$' -or $_.startLine -lt 1 -or $_.endLine -lt $_.startLine})
  if(@($logical.sourceTrace).Count -ne 13 -or $badLogicalTrace.Count -ne 0){throw "Logical source trace is incomplete or malformed: count=$(@($logical.sourceTrace).Count), invalid=$($badLogicalTrace.Count)"}
  $dependencyKinds=@($inventory.memberDependencies|Select-Object -ExpandProperty kind -Unique)
  foreach($kind in @('method-call','object-creation','field-reference','property-reference','event-reference')){
    if($kind -notin $dependencyKinds){throw "Expected semantic dependency kind missing: $kind"}
  }
  if(@($inventory.memberDependencies|Where-Object{$_.sourceMember -notmatch '^\w+\.' -or $_.line -lt 1}).Count -ne 0){throw 'Member dependency is missing its owning source member or line.'}
  if(@($inventory.memberDependencies|Where-Object{$_.targetFile -and $_.targetLine -lt 1}).Count -ne 0){throw 'Internal dependency target has an invalid source line.'}
  $after=@($paths|ForEach-Object{(Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash})
  if(($before -join ',') -ne ($after -join ',')){throw 'Input sources changed during analysis.'}
  Write-Output "PASS: 7 files/types, 166 unique dispatch entries, 166 Roslyn method bindings, ADD (4/9/8), ADC (6/9/14), SBB (7/9/15 plus spec AC), SUB/SUI (2/11/10 plus spec AC), DAD (4/8/6), logical (9/20/13 plus spec operations), $($inventory.memberDependencies.Count) source-mapped dependencies; other opcode semantics TODO; input hashes unchanged."
}
finally {
  $resolved=[IO.Path]::GetFullPath($temp)
  if(-not $resolved.StartsWith($artifactsRoot.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)){throw "Unsafe test cleanup path: $resolved"}
  if(Test-Path -LiteralPath $resolved){if(((Get-Item -LiteralPath $resolved -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0){throw "Refusing linked test cleanup path: $resolved"};Remove-Item -LiteralPath $resolved -Recurse -Force}
}
