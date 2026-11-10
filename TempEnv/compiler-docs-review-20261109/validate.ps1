$ErrorActionPreference='Stop'
$root='C:/Users/Yuriy/Desktop/HybridCPU ISE'
$plan="$root/Documentation/Compilers/HybridCPU_Compiler_vNext_Plan_v3"
$white="$root/Documentation/Compilers/Whitebook"
$snapshot="$root/TempEnv/doom-continuation-20260905/scalar-default-20261024"
$results=[ordered]@{}
foreach($name in 'compiler-source-final.json','evaluated-source-final.json'){
 $rows=Get-Content "$snapshot/$name" -Raw|ConvertFrom-Json
 $bad=@(foreach($row in $rows){if(!(Test-Path -LiteralPath $row.Path) -or (Get-FileHash -LiteralPath $row.Path).Hash -ne $row.Hash){$row.Path}})
 $results[$name]=@{Count=$rows.Count;Drift=$bad.Count;Paths=$bad}
 if($bad.Count){throw "Snapshot source drift: $name"}
}
$previous=@(Get-Content "$snapshot/compiler-source-final.json" -Raw|ConvertFrom-Json|ForEach-Object {[IO.Path]::GetFullPath($_.Path)})
$current=@(Get-ChildItem "$root/Compilers/HybridCPU_Compiler" -Recurse -File|Where-Object {$_.FullName -notmatch '\\(bin|obj)\\' -and $_.Extension -in '.cs','.csproj','.props','.targets'}|ForEach-Object {$_.FullName})
$setDifference=@(Compare-Object $previous $current)
$results['compiler_source_inventory']=@{Count=$current.Count;SetDifference=$setDifference}
if($setDifference.Count){throw 'Compiler source inventory differs'}
$files=@(Get-ChildItem $plan -Recurse -File -Filter '*.md')+@(Get-ChildItem $white -Recurse -File -Filter '*.md')
$broken=@();$links=0;$whitespace=@()
foreach($file in $files){
 $text=[IO.File]::ReadAllText($file.FullName)
 if($text -match '(?m)[ \t]+\r?$'){$whitespace+=$file.FullName}
 foreach($match in [regex]::Matches($text,'\]\((<[^>]+>|[^)]+)\)')){
  $target=[Uri]::UnescapeDataString($match.Groups[1].Value.Trim('<','>').Split('#')[0])
  if(!$target -or $target -match '^[a-z]+:'){continue};$links++
  if(!(Test-Path -LiteralPath (Join-Path $file.DirectoryName $target))){$broken+=[pscustomobject]@{File=$file.Name;Target=$target}}
 }
}
$results['documents']=@{Files=$files.Count;LocalLinks=$links;Broken=$broken;TrailingWhitespace=$whitespace}
$manifest=Get-Content "$plan/plan_manifest.json" -Raw|ConvertFrom-Json
foreach($field in 'current_status_file','capability_report','whitebook','acceptance_file','source_index','validation_file','archive'){
 if(!(Test-Path -LiteralPath (Join-Path $plan $manifest.$field))){throw "Manifest path $field"}
}
foreach($phase in $manifest.phases){if(!(Test-Path -LiteralPath "$plan/$($phase.file)")){throw 'Phase path'}}
$results['manifest']=@{Parsed=$true;PhaseFiles=$manifest.phases.Count;Qualification=$manifest.qualification}
$results|ConvertTo-Json -Depth 8|Set-Content "$PSScriptRoot/validation.json"
if($broken.Count){$broken|ConvertTo-Json;throw 'Broken documentation links'}
if($whitespace.Count){throw 'Trailing whitespace'}
$checks=Get-ChildItem $plan -Recurse -File|Where-Object Name -ne CHECKSUMS.sha256|Sort-Object FullName
$checks|ForEach-Object {"$((Get-FileHash $_.FullName).Hash.ToLowerInvariant())  $([IO.Path]::GetRelativePath($plan,$_.FullName).Replace('\','/'))"}|Set-Content "$plan/CHECKSUMS.sha256"
Get-ChildItem $white -File|Where-Object Name -ne CHECKSUMS.sha256|Sort-Object Name|ForEach-Object {"$((Get-FileHash $_.FullName).Hash.ToLowerInvariant())  $($_.Name)"}|Set-Content "$white/CHECKSUMS.sha256"
foreach($directory in $plan,$white){foreach($line in Get-Content "$directory/CHECKSUMS.sha256"){
 if($line -notmatch '^([0-9a-f]{64})  (.+)$'){throw 'Checksum format'};$hash=$Matches[1];$name=$Matches[2]
 if((Get-FileHash -LiteralPath "$directory/$name").Hash.ToLowerInvariant() -ne $hash){throw 'Checksum mismatch'}
}}
& git diff --check -- $plan $white *> "$PSScriptRoot/diff-check.log"
if($LASTEXITCODE){throw 'Diff whitespace check'}
$results['checksums']=@{Plan=$checks.Count;Whitebook=1;Verified=$true;DiffCheckExit=0}
$results|ConvertTo-Json -Depth 8|Set-Content "$PSScriptRoot/validation.json"
Get-Content "$PSScriptRoot/validation.json"
