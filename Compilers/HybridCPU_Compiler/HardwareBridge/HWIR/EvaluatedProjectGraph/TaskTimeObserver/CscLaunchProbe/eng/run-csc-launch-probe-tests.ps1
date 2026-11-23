param([string]$SdkDotnet = 'C:\Users\Yuriy\AppData\Local\Temp\cw-integration-sdk-rc2\dotnet.exe')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('cw-csc-launch-probe-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tempRoot | Out-Null
try {
    $env:DOTNET_ROOT = Split-Path -Parent $SdkDotnet
    $dotnet = (Resolve-Path -LiteralPath $SdkDotnet).Path
    Push-Location $tempRoot
    $sdk = (& $dotnet --version).Trim()
    if ($sdk -ne '11.0.100-rc.2.26504.105') { throw "Unexpected pinned SDK: $sdk" }

    $probeProject = Join-Path $root 'CscLaunchProbe.csproj'
    $fixtureProject = Join-Path $root 'FixtureCsc\FixtureCsc.csproj'
    $testsProject = Join-Path $root 'Tests\CscLaunchProbe.Tests.csproj'
    $projects = @(
        @{ Path = $probeProject; Name = 'probe' },
        @{ Path = $fixtureProject; Name = 'fixture' },
        @{ Path = $testsProject; Name = 'tests' }
    )
    foreach ($project in $projects) {
        & $dotnet build $project.Path -c Release --nologo "-p:ArtifactsPath=$tempRoot"
        if ($LASTEXITCODE -ne 0) { throw "Build failed: $($project.Name) exit $LASTEXITCODE" }
    }

    $probeExe = Join-Path $tempRoot 'bin\CscLaunchProbe\CscLaunchProbe.exe'
    $fixtureExe = Join-Path $tempRoot 'bin\FixtureCsc\FixtureCsc.exe'
    $testsDll = Join-Path $tempRoot 'bin\CscLaunchProbe.Tests\CscLaunchProbe.Tests.dll'
    if (!(Test-Path $probeExe) -or !(Test-Path $fixtureExe) -or !(Test-Path $testsDll)) { throw 'Expected TEMP outputs missing.' }
    $env:CW_CSC_PROBE_EXE = $probeExe
    $env:CW_CSC_FIXTURE_EXE = $fixtureExe
    & $dotnet $testsDll
    if ($LASTEXITCODE -ne 0) { throw "Fixture suite failed: exit $LASTEXITCODE" }
    'CSC_LAUNCH_PROBE_FIXTURES_PASSED'
}
finally {
    Pop-Location
    Remove-Item Env:CW_CSC_PROBE_EXE -ErrorAction SilentlyContinue
    Remove-Item Env:CW_CSC_FIXTURE_EXE -ErrorAction SilentlyContinue
    Remove-Item Env:DOTNET_ROOT -ErrorAction SilentlyContinue
    # This path is freshly allocated by this script and lives directly under TEMP.
    Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
}
