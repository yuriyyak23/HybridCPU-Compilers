[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9a-f]{40}$')]
    [string] $CompilerCommit,
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9a-f]{64}$')]
    [string] $Phase26EvidenceSha256,
    [string] $OutputRoot
)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'HybridCPU.Compiler.Release.csproj'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
if (!$OutputRoot) {
    $OutputRoot = Join-Path $repo "TempEnv\doom-continuation-20260905\release-$([DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss'))-$([Guid]::NewGuid().ToString('N'))"
}
$verificationRoot = [IO.Path]::GetFullPath($OutputRoot)
if (Test-Path -LiteralPath $verificationRoot) { throw 'HCRL1206: output root must be new.' }
New-Item -ItemType Directory -Path $verificationRoot | Out-Null
$savedEnvironment = @{}
foreach ($name in @('TEMP','TMP','DOTNET_CLI_HOME','NUGET_PACKAGES')) {
    $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}
try {
    foreach ($dir in @('temp','cli','packages','logs')) { New-Item -ItemType Directory -Path (Join-Path $verificationRoot $dir) | Out-Null }
    $env:TEMP = Join-Path $verificationRoot 'temp'
    $env:TMP = $env:TEMP
    $env:DOTNET_CLI_HOME = Join-Path $verificationRoot 'cli'
    $env:NUGET_PACKAGES = Join-Path $verificationRoot 'packages'
    $properties = @('-p:Configuration=Release', "-p:CustomBeforeDirectoryBuildProps=$repo\Compilers\CpuInterfaceBridge\Validation\Isolation.props", "-p:BridgeValidationRoot=$verificationRoot")
    Get-CimInstance Win32_Process | Where-Object { $_.Name -match 'dotnet|Doom|Hybrid|ISE' } |
        Select-Object ProcessId,Name,CreationDate,ExecutablePath,CommandLine |
        ConvertTo-Json -Depth 4 | Set-Content (Join-Path $verificationRoot 'logs\process-preflight.json')
    & dotnet build $project @properties -p:UseSharedCompilation=false -m:1 1> (Join-Path $verificationRoot 'logs\build.stdout.txt') 2> (Join-Path $verificationRoot 'logs\build.stderr.txt')
    $buildExit = $LASTEXITCODE
    Set-Content (Join-Path $verificationRoot 'logs\build.exit.txt') $buildExit
    if ($buildExit -ne 0) { throw "HCRL1201: release qualification tool build failed; see $verificationRoot/logs." }
    $tool = (& dotnet msbuild $project @properties -getProperty:TargetPath 2> (Join-Path $verificationRoot 'logs\target-path.stderr.txt') | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or !(Test-Path -LiteralPath $tool)) { throw 'HCRL1207: evaluated tool output is unavailable.' }
    Set-Content (Join-Path $verificationRoot 'logs\target-path.txt') $tool
    $outputs = @()
    foreach ($run in 1..2) {
        $manifest = Join-Path $verificationRoot "run-$run\release-manifest.json"
        & dotnet $tool --commit $CompilerCommit --phase26-evidence $Phase26EvidenceSha256 --out $manifest 1> (Join-Path $verificationRoot "logs\run-$run.stdout.txt") 2> (Join-Path $verificationRoot "logs\run-$run.stderr.txt")
        $runExit = $LASTEXITCODE
        Set-Content (Join-Path $verificationRoot "logs\run-$run.exit.txt") $runExit
        if ($runExit -ne 0) { throw "HCRL1202: release qualification run $run failed." }
        $telemetry = Get-Content -Raw -LiteralPath ($manifest + '.telemetry.json') | ConvertFrom-Json
        if ($telemetry.SelectsProductionOutput) { throw 'HCRL1203: telemetry was marked as production-selecting.' }
        $outputs += [ordered]@{
            manifest = (Get-FileHash -Algorithm SHA256 -LiteralPath $manifest).Hash.ToLowerInvariant()
            features = (Get-FileHash -Algorithm SHA256 -LiteralPath ($manifest + '.features.json')).Hash.ToLowerInvariant()
            compiler = (Get-FileHash -Algorithm SHA256 -LiteralPath ($manifest + '.compiler.json')).Hash.ToLowerInvariant()
            compileMilliseconds = [double]$telemetry.CompileMilliseconds
            peakWorkingSetBytes = [long]$telemetry.PeakWorkingSetBytes
        }
    }
    if ($outputs[0].manifest -ne $outputs[1].manifest -or $outputs[0].features -ne $outputs[1].features -or $outputs[0].compiler -ne $outputs[1].compiler) {
        throw 'HCRL1204: deterministic release artifacts differ.'
    }
    [ordered]@{
        schema = 'hybridcpu.phase27-release-verification/v1'
        compilerCommit = $CompilerCommit
        phase26EvidenceSha256 = $Phase26EvidenceSha256
        deterministicRuns = 2
        releaseManifestSha256 = $outputs[0].manifest
        featureMatrixSha256 = $outputs[0].features
        compilerIdentitySha256 = $outputs[0].compiler
        compileMillisecondsTelemetry = @($outputs[0].compileMilliseconds, $outputs[1].compileMilliseconds)
        peakWorkingSetBytesTelemetry = @($outputs[0].peakWorkingSetBytes, $outputs[1].peakWorkingSetBytes)
        telemetrySelectsProductionOutput = $false
    } | ConvertTo-Json | Tee-Object -FilePath (Join-Path $verificationRoot 'verification.json')
}
finally {
    foreach ($name in $savedEnvironment.Keys) {
        [Environment]::SetEnvironmentVariable($name, $savedEnvironment[$name], 'Process')
    }
}
