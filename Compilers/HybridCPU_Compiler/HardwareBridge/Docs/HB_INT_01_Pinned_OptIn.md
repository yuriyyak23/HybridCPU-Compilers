# HB-INT-01 — pinned opt-in HWIR compatibility (S1)

**Scope:** HardwareBridge project configuration, not Golden, Core reader, RTL, or verification. Follows [HB #5](https://github.com/yuriyyak23/HybridCPU-Compilers/pull/5), pairs with [CW-INT-02 Core #2](https://github.com/yuriyyak23/CircuitWeave/pull/2), and references [HB plan #4](https://github.com/yuriyyak23/HybridCPU-Compilers/pull/4).

## Modes and contract versions

HardwareBridge.Companion.csproj remains **unchanged**, opt-in, HARDWAREBRIDGE_COMPANION, and exports read-only SourceSnapshot, CompilerFacts and allowlisted GoldenOracle. Wire contracts remain hybridcpu.hardwarebridge.source-snapshot/v1 and hybridcpu.hardwarebridge.sidecar/v1. The parent software compiler has no new hardware project dependency.

By default HardwareBridge.Hwir.csproj compiles **only the existing source snapshot v1 DTO** without Core references. This minimal assembly does not implement legacy HWIR compilers. The unchanged specialized BridgeHwir source files are enabled only with EnableBridgeHwirLegacyFixtures=true. A build in that mode requires an explicit absolute Core root, Core frontend project, reviewed lowercase 40-hex Core HEAD and exact .NET SDK version. This is compatibility regression, not production RTL qualification.

## Reproduce on Windows PowerShell with independently named checkouts

Replace the paths and SHA with two separately reviewed checkouts and an *independently approved* Core commit. Do not treat the observed HEAD as its own approval pin.

~~~powershell
$bridgeRoot = (Resolve-Path '<path-to-Bridge-checkout>').Path
$coreRoot = (Resolve-Path '<path-to-Core-checkout>').Path
$reviewedCoreSha = '<reviewed-lowercase-40-hex-Core-commit>'
$expectedSdk = '11.0.100-rc.2.26504.105'
if ((git -C $coreRoot rev-parse HEAD).Trim() -cne $reviewedCoreSha) { throw 'Wrong Core revision' }
if ((dotnet --version).Trim() -cne $expectedSdk) { throw 'Wrong SDK' }
$hwir = Join-Path $bridgeRoot 'Compilers/HybridCPU_Compiler/HardwareBridge/HWIR/HardwareBridge.Hwir.csproj'
$frontend = Join-Path $coreRoot 'src/CircuitWeave.Frontend.Roslyn/CircuitWeave.Frontend.Roslyn.csproj'
$properties = @('-p:Configuration=Release', '-p:EnableBridgeHwirLegacyFixtures=true',
  "-p:CircuitWeaveRepoRoot=$coreRoot",
  "-p:CircuitWeaveFrontendRoslynProject=$frontend",
  "-p:CircuitWeaveExpectedHeadSha=$reviewedCoreSha",
  "-p:BridgeExpectedSdkVersion=$expectedSdk")
& dotnet build $hwir @properties
if ($LASTEXITCODE -ne 0) { throw "Fixture build failed: $LASTEXITCODE" }
$target = & dotnet msbuild $hwir @properties -nologo -getProperty:TargetPath
if ($LASTEXITCODE -ne 0) { throw "TargetPath evaluation failed: $LASTEXITCODE" }
Get-FileHash -LiteralPath $target -Algorithm SHA256
~~~

The ValidatePinnedBridgeHwirFixtures MSBuild target rejects missing root/frontend (HBINT0101/02), missing/wrong SHA and dirty/untracked Core checkout (HBINT0103), wrong/missing SDK (HBINT0104), and a frontend path that is not the selected Core root (HBINT0105). The Core ProjectReference is included only when explicitly opted-in. Standalone default HWIR and Companion builds do not depend on the name or location of a sibling Core checkout.

## Test and migration gates

Static positive/mutation cases: python3 Compilers/HybridCPU_Compiler/HardwareBridge/Tests/PortableDependencyBoundary.Tests.py. They check project-graph conditions, portable references, default parent compiler exclusion and deliberate mutations. A Python test exit 0 is not .NET build or Golden evidence. For a real integration receipt, capture dotnet --info, full command line, actual process exit, complete raw output and SHA-256 of the resulting DLL and logs. Compare actual Golden read-only checkout C# file SHA-256 before and after from an external workspace; missing Golden checkout is NotRun. Source-linked P14 scalar fixtures remain compatible in their opt-in mode but are not production closure.

The Core launcher `eng/cw-int-02-bridge-compat.sh` supplies the Core root, frontend path, reviewed Core SHA and exact SDK. Its current working copy also passes `EnableBridgeHwirLegacyFixtures=true` explicitly. The legacy Bridge build passed on the historical clean Core pin `555672232e46cb4e8b157e2d1b1db1e873c4194e` with the exact RC2 SDK. A paired run on a new reviewed Core commit remains open. [Current audit](HardwareBridge_Modernization_Audit_20261121.md).

The Core-owned circuitweave.bridge-import/v1 reader and typed MissingBridge remain outside Bridge ownership. Candidate HWIR, independent RTL acceptance, target observations and Physical signoff are separate. Exit codes, SHA-256 and Studio snapshots are not Verified. CPU top remains INCOMPLETE. Native ToolBridge pins, Golden symlink/no-write checks, isolated native timeout/cancellation, real Windows SDK and physical runs are NotRun without actual receipts.


### HB-INT-01 current portable/pin recheck — 2026-11-23

Current Bridge bytes preserve portable HWIR as source-snapshot-only with no default ProjectReference. Fresh .NET 11.0.100 Release default build passed with zero warnings/errors; `PortableDependencyBoundary.Tests.py` and `HWIR/Tests/test_parity_guard.py` passed. Empty and mismatched SDK pins refuse with `HBINT0104`; missing Core root/frontend refuses with `HBINT0101`. Positive pinned opt-in is carried by the existing current-Core build receipt: the clean Core clone is HEAD `9d0b4b9f50ab30bba3bea5eb2d59a4509cf0f435`, and the current project/pin-test bytes match the recorded Bridge input inventory. [Recheck receipt/logs](C:/Users/Yuriy/Desktop/CircuitWeave-main/artifacts/processor-20261114/bridge-portable-gate-20261123/receipt.json), SHA-256 `7dc52b9f4b8abd439fe46de27bf2e860b641dda62be126847b85f6e0c65c9299`.

This closes local portable/pinned build mechanics only. Hosted CI, protected branch/trusted handoff, Golden execution, CWR3 complete Processor scope, RTL/simulation, and Processor equivalence remain open.
