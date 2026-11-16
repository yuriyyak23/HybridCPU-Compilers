param(
    [string]$ProducerRoot = 'C:\Users\Yuriy\Desktop\CircuitWeave-main',
    [string]$ModelRoot = 'C:\Users\Yuriy\Desktop\HybridCPU ISE\HybridCPU_ISE',
    [string]$CirctBin = 'C:\Users\Yuriy\Desktop\CircuitWeave ExTool\final\packages\circt-firtool-1.163.0\firtool-1.163.0\bin',
    [string]$OssBin = 'C:\Users\Yuriy\Desktop\CircuitWeave ExTool\final\packages\oss-cad-suite-2026-11-10\oss-cad-suite\bin',
    [string]$ArtifactName = 'csr-storage-event-rtl-v12'
)
$ErrorActionPreference = 'Stop'
$bridgeRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$project = Join-Path $PSScriptRoot 'CsrStorageEvent.Runner.csproj'
$runtimeProject = Join-Path $PSScriptRoot 'RuntimeOracle\CsrStorageEvent.RuntimeOracle.csproj'
$bridgeOutput = Join-Path $bridgeRoot '.artifacts\csr-storage-event-v1'
$goldenRoot = Join-Path $ProducerRoot 'artifacts\processor-20261114\golden-csr-storage-runtime-v1'
$artifact = Join-Path $ProducerRoot (Join-Path 'artifacts\processor-20261114' $ArtifactName)
if (Test-Path -LiteralPath $artifact) { throw "Immutable output already exists: $artifact" }
New-Item -ItemType Directory -Path $artifact | Out-Null

dotnet run --project $project --configuration Release -- --prepare-rtl-inputs
if ($LASTEXITCODE -ne 0) { throw 'CsrStorageEvent source/HWIR preparation failed.' }

$activeCsrRoot = Join-Path $ModelRoot 'CloseToHSL\Core\Architecture\Registers\CSR'
$activeAddress = Join-Path $ModelRoot 'Arch\CsrAddresses.cs'
$expectedSources = @(
    @{Path=(Join-Path $activeCsrRoot 'CsrFile.cs'); Hash='2dc2c172d8248ad2fed0a3832b39008b0449f302e75280b8fef46b2d499ad470'; Name='active-CsrFile.cs'},
    @{Path=(Join-Path $activeCsrRoot 'CsrAccessPolicy.cs'); Hash='4d899b1daa5442738b89730ff00d856cca381a81a7b7b1df65da008d78cc93d5'; Name='active-CsrAccessPolicy.cs'},
    @{Path=$activeAddress; Hash='a2be00ba7d3248bc0aa4e3a49e238a5e0afdd919b5cf1c0d9d2e0bfa01893ef9'; Name='active-CsrAddresses.cs'},
    @{Path=(Join-Path $goldenRoot 'CsrFile.cs'); Hash='a11957487b261eaf5c655a8c7184266ddc1822a1d6ca870c22f45a94ad50d74f'; Name='golden-CsrFile.cs'},
    @{Path=(Join-Path $goldenRoot 'CsrAccessPolicy.cs'); Hash='4d899b1daa5442738b89730ff00d856cca381a81a7b7b1df65da008d78cc93d5'; Name='golden-CsrAccessPolicy.cs'},
    @{Path=(Join-Path $goldenRoot 'CsrAddresses.cs'); Hash='a2be00ba7d3248bc0aa4e3a49e238a5e0afdd919b5cf1c0d9d2e0bfa01893ef9'; Name='golden-CsrAddresses.cs'}
)
$runtimeSources = foreach ($source in $expectedSources) {
    if (-not (Test-Path -LiteralPath $source.Path -PathType Leaf)) { throw "Missing exact CSR runtime source: $($source.Path)" }
    $actualHash = (Get-FileHash -LiteralPath $source.Path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualHash -ne $source.Hash) { throw "CSR runtime source hash mismatch: $($source.Path) expected=$($source.Hash) actual=$actualHash" }
    [ordered]@{name=$source.Name;path=[IO.Path]::GetFullPath($source.Path);sha256=$actualHash}
}
$runtimeProvenance = [ordered]@{
    schema='circuitweave.csr-runtime-source-provenance/v1'
    goldenCommit='6946ccab9d888f7dae0a4d8e18480f436138acd9'
    activeModelCommit='1bfe41a49cf11c80fa6c6aee87accb54b78b331f'
    runtimeSourceFiles=@($runtimeSources)
    runtimeHarness=@(foreach ($relative in @('RuntimeOracle\Program.cs','RuntimeOracle\RuntimeStubs.cs','RuntimeOracle\CsrStorageEvent.RuntimeOracle.csproj','Program.cs','CsrStorageEvent.Runner.csproj','run_rtl_crosscheck.py','Run-CsrStorageEventRtl.ps1')) {
        $runtimePath = Join-Path $PSScriptRoot $relative
        [ordered]@{path=[IO.Path]::GetFullPath($runtimePath);sha256=(Get-FileHash -LiteralPath $runtimePath -Algorithm SHA256).Hash.ToLowerInvariant()}
    })
    dependencyBoundary='Exact CsrFile, CsrAccessPolicy, CsrAddresses bytes are compiled. RuntimeStubs supplies only CorePowerState constants C0_Active=0 and P0_MaxPerformance=100, checked against pinned CPU_Core.FSM.cs source SHA-256 43214e337d4640febb6877460df4478ad74bd7c63dfb71be6f410b1a7e05a54f.'
}
$runtimeProvenancePath = Join-Path $artifact 'runtime-source-provenance.json'
[IO.File]::WriteAllText($runtimeProvenancePath, (($runtimeProvenance | ConvertTo-Json -Depth 8) + "`n"), [Text.UTF8Encoding]::new($false))
$activeTrace = Join-Path $artifact 'active-runtime.json'
$goldenTrace = Join-Path $artifact 'golden-runtime.json'
dotnet run --project $runtimeProject --configuration Release "-p:RuntimeModel=ACTIVE" "-p:CsrSourceRoot=$activeCsrRoot" "-p:AddressSource=$activeAddress" -- (Join-Path $bridgeOutput 'csr-register-inventory.json') $activeTrace
if ($LASTEXITCODE -ne 0) { throw 'Exact active CsrFile runtime trace failed.' }
dotnet run --project $runtimeProject --configuration Release "-p:RuntimeModel=GOLDEN" "-p:CsrSourceRoot=$goldenRoot" "-p:AddressSource=$(Join-Path $goldenRoot 'CsrAddresses.cs')" -- (Join-Path $bridgeOutput 'csr-register-inventory.json') $goldenTrace
if ($LASTEXITCODE -ne 0) { throw 'Exact pinned-Golden CsrFile runtime trace failed.' }

$python = Join-Path $PSScriptRoot 'run_rtl_crosscheck.py'
python $python --hwir (Join-Path $bridgeOutput 'csr-storage-event.hwir.json') `
    --inventory (Join-Path $bridgeOutput 'csr-register-inventory.json') `
    --mlir (Join-Path $bridgeOutput 'csr-storage-event.mlir') `
    --circt-bin $CirctBin --oss-bin $OssBin --active-runtime $activeTrace `
    --golden-runtime $goldenTrace --runtime-provenance $runtimeProvenancePath --output $artifact
if ($LASTEXITCODE -ne 0) { throw 'CIRCT/Icarus CSR storage event crosscheck failed.' }

$env:CSR_STORAGE_EVENT_RTL_ARTIFACT = $artifact
dotnet run --project $project --configuration Release -- --publish-rtl
if ($LASTEXITCODE -ne 0) { throw 'Studio publication with verified RTL evidence failed.' }
Write-Host "CSR storage event RTL and Studio candidate published: $artifact"
