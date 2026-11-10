[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',
    [switch] $SkipComponentShowcase
)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'HybridCpuAotAddDemo.csproj'

if (!$SkipComponentShowcase) {
    & dotnet run --project $project -c $Configuration -p:DemoMode=ComponentShowcase
    if ($LASTEXITCODE -ne 0) { throw 'RefPlan7 component showcase failed.' }
}

foreach ($corpus in 'ControlFlow', 'Loop', 'CallGraph', 'DirectRecursion', 'MutualRecursion', 'Generics') {
    $publishDirectory = Join-Path $PSScriptRoot "publish-refplan7\$corpus"
    & dotnet publish $project -c $Configuration -r hybridcpu -p:DemoMode=RestrictedAot `
        -p:DemoCorpus=$corpus "-p:PublishDir=$publishDirectory\"
    if ($LASTEXITCODE -ne 0) { throw "RefPlan7 AOT publication failed for corpus '$corpus'." }
}

Write-Output 'RefPlan7 component checks and all restricted AOT corpora completed successfully.'
