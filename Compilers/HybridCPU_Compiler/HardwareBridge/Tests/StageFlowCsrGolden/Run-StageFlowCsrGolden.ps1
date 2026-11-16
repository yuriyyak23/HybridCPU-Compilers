param(
    [string]$GoldenGitDir = "$env:TEMP\cw-golden-6946ccab-20261114",
    [string]$ModelRoot = 'C:\Users\Yuriy\Desktop\HybridCPU ISE\HybridCPU_ISE',
    [string]$RunRoot = "$env:TEMP\cw-stageflow-csr-golden-20261115"
)
$ErrorActionPreference = 'Stop'
$golden = '6946ccab9d888f7dae0a4d8e18480f436138acd9'
$csrPath = 'HybridCPU_ISE/CloseToHSL/Core/Architecture/Registers/CSR/CsrFile.cs'
$policyPath = 'HybridCPU_ISE/CloseToHSL/Core/Architecture/Registers/CSR/CsrAccessPolicy.cs'
$addressPath = 'HybridCPU_ISE/Arch/CsrAddresses.cs'
$flowPath = 'HybridCPU_ISE/CloseToHSL/Core/Pipeline/ExecutionFlow/StageFlow/CPU_Core.PipelineExecution.StageFlow.cs'
$activeCsrPath = Join-Path $ModelRoot 'CloseToHSL\Core\Architecture\Registers\CSR\CsrFile.cs'
$activePolicyPath = Join-Path $ModelRoot 'CloseToHSL\Core\Architecture\Registers\CSR\CsrAccessPolicy.cs'
$activeAddressPath = Join-Path $ModelRoot 'Arch\CsrAddresses.cs'
$activeFlowPath = Join-Path $ModelRoot 'CloseToHSL\Core\Pipeline\ExecutionFlow\StageFlow\CPU_Core.PipelineExecution.StageFlow.cs'

function Get-GoldenText([string]$path) {
    $text = & git "--git-dir=$GoldenGitDir" show "${golden}:$path"
    if ($LASTEXITCODE -ne 0) { throw "Golden source unavailable: $path at $golden" }
    return ($text -join "`n") + "`n"
}
function Get-Prefix([string]$text, [string]$method, [string]$stop) {
    $methodAt = $text.IndexOf($method, [StringComparison]::Ordinal)
    if ($methodAt -lt 0) { throw "Method marker missing: $method" }
    $open = $text.IndexOf('{', $methodAt)
    $end = $text.IndexOf($stop, $open, [StringComparison]::Ordinal)
    if ($open -lt 0 -or $end -lt 0) { throw "Bounded method prefix missing: $method" }
    return $text.Substring($open + 1, $end - $open - 1)
}
function Assert-Prefix([string]$prefix, [string]$label) {
    $normalized = [regex]::Replace($prefix, '\s+', ' ').Trim()
    $expected = 'if (!pipeCtrl.Enabled) return; pipeCtrl.CycleCount++; Csr.HardwareWrite(CsrAddresses.Cycle, pipeCtrl.CycleCount);'
    if ($normalized -ne $expected) { throw "$label StageFlow prefix changed: $normalized" }
}

$goldenSource = @{
    CsrFile = Get-GoldenText $csrPath
    Policy = Get-GoldenText $policyPath
    Addresses = Get-GoldenText $addressPath
    Flow = Get-GoldenText $flowPath
}
$activeSource = @{
    CsrFile = [IO.File]::ReadAllText($activeCsrPath)
    Policy = [IO.File]::ReadAllText($activePolicyPath)
    Addresses = [IO.File]::ReadAllText($activeAddressPath)
    Flow = [IO.File]::ReadAllText($activeFlowPath)
}
$goldenPrefix = Get-Prefix $goldenSource.Flow 'public void ExecutePipelineCycle()' 'var memorySubsystem = GetBoundMemorySubsystem();'
$activePrefix = Get-Prefix $activeSource.Flow 'private void ExecuteUnparkedPipelineCycle()' 'MemorySubsystem memorySubsystem = GetBoundMemorySubsystem();'
Assert-Prefix $goldenPrefix 'Golden'
Assert-Prefix $activePrefix 'Active'
foreach ($name in 'Policy','Addresses') {
    $ah = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($activeSource[$name]))).ToLowerInvariant()
    $gh = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($goldenSource[$name]))).ToLowerInvariant()
    if ($ah -ne $gh) { throw "$name source differs between active and pinned Golden; audit its contract before comparing CsrFile." }
}

if (Test-Path -LiteralPath $RunRoot) { $RunRoot = "$RunRoot-$(Get-Date -Format 'yyyyMMdd-HHmmss')-$([guid]::NewGuid().ToString('N').Substring(0,8))" }
New-Item -ItemType Directory -Path $RunRoot | Out-Null
$programTemplate = @'
using System.Text.Json;
using YAKSys_Hybrid_CPU.Arch;
using YAKSys_Hybrid_CPU.Core.Registers;
using YAKSys_Hybrid_CPU.Processor;
var results = new List<object>();
foreach (var vector in new[] { ("disabled", false, false, 11UL), ("enabled_zero", true, false, 0UL), ("enabled_normal", true, false, 41UL), ("enabled_wrap", true, false, ulong.MaxValue), ("reservation_failure", true, true, 5UL) })
{
    var core = new CPU_Core(vector.Item3, vector.Item4);
    string? error = null;
    try { core.Tick(vector.Item2); } catch (Exception ex) { error = ex.GetType().Name; }
    ulong stored = core.ReadCycle();
    ulong expectedCount = vector.Item2 ? unchecked(vector.Item4 + 1) : vector.Item4;
    ulong expectedStored = vector.Item2 ? expectedCount : vector.Item4;
#if ACTIVE
    if (vector.Item3) expectedStored = vector.Item4;
    bool expectedError = vector.Item3;
#else
    bool expectedError = false;
#endif
    if (core.pipeCtrl.CycleCount != expectedCount || stored != expectedStored ||
        (expectedError && error != "InvalidOperationException") || (!expectedError && error is not null))
        throw new InvalidOperationException($"{vector.Item1}: count={core.pipeCtrl.CycleCount}/{expectedCount} csr={stored}/{expectedStored} error={error}");
    results.Add(new { vector = vector.Item1, enabled = vector.Item2, reservationFailure = vector.Item3, before = vector.Item4, cycleCountAfter = core.pipeCtrl.CycleCount, csrAfter = stored, error });
}
var sequenceResults = new List<object>();
var sequenceCore = new CPU_Core(true, 41UL);
foreach (var step in new[] { ("reserved_write_failure", true), ("disabled_hold", false), ("recovery_write", true), ("continued_write", true), ("continued_disabled_hold", false) })
{
    string? error = null;
    try { sequenceCore.Tick(step.Item2); } catch (Exception ex) { error = ex.GetType().Name; }
    ulong stored = sequenceCore.ReadCycle();
    sequenceResults.Add(new { vector = step.Item1, enabled = step.Item2, cycleCountAfter = sequenceCore.pipeCtrl.CycleCount, csrAfter = stored, error });
}
Console.WriteLine(JsonSerializer.Serialize(new { independentVectors = results, sequence = sequenceResults }));

namespace YAKSys_Hybrid_CPU.Processor
{
public sealed class CPU_Core
{
    private bool _failReservation;
    public readonly PipeControl pipeCtrl = new();
    public readonly CsrFile Csr;
    public CPU_Core(bool failReservation, ulong initial)
    {
#if ACTIVE
        Csr = new CsrFile(_ => _failReservation ? throw new InvalidOperationException("reservation-denied") : new AccessLease());
#else
        Csr = new CsrFile();
#endif
        Csr.HardwareWrite(CsrAddresses.Cycle, initial);
        pipeCtrl.CycleCount = initial;
        _failReservation = failReservation;
    }
    public void Tick(bool enabled) { pipeCtrl.Enabled = enabled; ExecuteProjection(); }
    public ulong ReadCycle() { _failReservation = false; return Csr.DirectRead(CsrAddresses.Cycle); }
    private void ExecuteProjection()
    {
__PREFIX__
    }
    public sealed class PipeControl { public bool Enabled; public ulong CycleCount; }
    private sealed class AccessLease : IDisposable { public void Dispose() { } }
    public enum CorePowerState { C0_Active, P0_MaxPerformance }
}
}
'@
$projectTemplate = @'
<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net11.0</TargetFramework><OutputType>Exe</OutputType><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup></Project>
'@
function New-And-Run([string]$name, [hashtable]$sources, [string]$prefix, [bool]$active) {
    $dir = Join-Path $RunRoot $name; New-Item -ItemType Directory -Path $dir | Out-Null
    foreach ($key in 'CsrFile','Policy','Addresses') { [IO.File]::WriteAllText((Join-Path $dir "$key.cs"), $sources[$key], [Text.UTF8Encoding]::new($false)) }
    $program = $programTemplate.Replace('__PREFIX__', $prefix)
    [IO.File]::WriteAllText((Join-Path $dir 'Program.cs'), $program, [Text.UTF8Encoding]::new($false))
    $proj = $projectTemplate
    if ($active) { $proj = $proj.Replace('<Nullable>enable</Nullable>', '<Nullable>enable</Nullable><DefineConstants>ACTIVE</DefineConstants>') }
    [IO.File]::WriteAllText((Join-Path $dir "$name.csproj"), $proj, [Text.UTF8Encoding]::new($false))
    $lines = & dotnet run --project (Join-Path $dir "$name.csproj") --configuration Release
    if ($LASTEXITCODE -ne 0) { throw "$name executable prefix harness failed." }
    $jsonLine = $lines | Select-Object -Last 1
    return ,($jsonLine | ConvertFrom-Json)
}
$goldenRun = New-And-Run 'Golden' $goldenSource $goldenPrefix $false
$activeRun = New-And-Run 'Active' $activeSource $activePrefix $true
$goldenResults = $goldenRun.independentVectors
$activeResults = $activeRun.independentVectors
$goldenSequence = $goldenRun.sequence
$activeSequence = $activeRun.sequence
foreach ($i in 0..3) {
    foreach ($field in 'cycleCountAfter','csrAfter','error') {
        if ($goldenResults[$i].$field -ne $activeResults[$i].$field) { throw "Active/Golden mismatch for $($goldenResults[$i].vector).$field" }
    }
}
if ($goldenResults[4].csrAfter -ne $goldenResults[4].cycleCountAfter -or
    $activeResults[4].csrAfter -eq $activeResults[4].cycleCountAfter -or
    $activeResults[4].error -ne 'InvalidOperationException') { throw 'Reservation failure characterization changed.' }
if ($activeSequence.Count -ne 5 -or $goldenSequence.Count -ne 5) { throw 'Sequential trace length changed.' }
if ($activeSequence[0].cycleCountAfter -ne 42 -or $activeSequence[0].csrAfter -ne 41 -or $activeSequence[0].error -ne 'InvalidOperationException' -or
    $activeSequence[1].cycleCountAfter -ne 42 -or $activeSequence[1].csrAfter -ne 41 -or
    $activeSequence[2].cycleCountAfter -ne 43 -or $activeSequence[2].csrAfter -ne 43 -or
    $activeSequence[3].cycleCountAfter -ne 44 -or $activeSequence[3].csrAfter -ne 44 -or
    $activeSequence[4].cycleCountAfter -ne 44 -or $activeSequence[4].csrAfter -ne 44) { throw 'Active sequence transition mismatch.' }
if ($goldenSequence[0].cycleCountAfter -ne 42 -or $goldenSequence[0].csrAfter -ne 42 -or $goldenSequence[0].error -or
    $goldenSequence[1].cycleCountAfter -ne 42 -or $goldenSequence[1].csrAfter -ne 42 -or
    $goldenSequence[2].cycleCountAfter -ne 43 -or $goldenSequence[2].csrAfter -ne 43 -or
    $goldenSequence[3].cycleCountAfter -ne 44 -or $goldenSequence[3].csrAfter -ne 44 -or
    $goldenSequence[4].cycleCountAfter -ne 44 -or $goldenSequence[4].csrAfter -ne 44) { throw 'Golden sequence transition mismatch.' }
$receipt = [ordered]@{
    schema = 'circuitweave.stageflow-csr-active-golden-prefix-check/v1'
    status = 'passed-bounded-prefix'
    goldenCommit = $golden
    goldenCsrFileSha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($goldenSource.CsrFile))).ToLowerInvariant()
    activeCsrFileSha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($activeSource.CsrFile))).ToLowerInvariant()
    stageFlowPrefix = 'enabled guard; ulong increment; HardwareWrite(Cycle,incremented count) before memory subsystem'
    equivalentVectors = 4
    sequenceSteps = 5
    activeSequence = $activeSequence
    goldenSequence = $goldenSequence
    sequenceResult = 'PASS: active exception divergence and disabled hold were retained; next successful write reconverged CycleCount and CSR.Cycle.'
    reservationFailure = 'Active reservation exception occurs after CycleCount increment but before CSR mutation; Golden CsrFile has no reservation hook and commits the write.'
    goldenResults = $goldenResults
    activeResults = $activeResults
    limits = 'Exact extracted StageFlow prefixes execute with exact active and pinned-Golden CsrFile, CsrAccessPolicy and CsrAddresses source copies. This is software behavior of a bounded call prefix; it is not physical clock/reset, whole pipeline, RTL equivalence or a complete memory/trap/replay trace.'
}
$receiptPath = Join-Path $RunRoot 'stageflow-csr-active-golden-prefix-check.json'
[IO.File]::WriteAllText($receiptPath, ($receipt | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
Write-Output "PASS: 4 common active-vs-Golden vectors; reservation exception divergence characterized. Receipt: $receiptPath"
