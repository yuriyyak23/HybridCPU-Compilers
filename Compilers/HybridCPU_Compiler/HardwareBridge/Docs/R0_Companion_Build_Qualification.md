# R0-01 — Companion production build qualification

## Scope and trust boundary

Owner: R0-01 only. The **real** build dependency chain is
`HardwareBridge.Companion.csproj -> HybridCPU.Compiler.Roslyn.csproj -> HybridCPU.Compiler.Cil.csproj -> HybridCPU.Compiler.Core.csproj`.
The Bridge project has no CircuitWeave project reference. No Golden or CircuitWeave checkout is performed in this CI lane. A successful process, synthetic test or matching digest **does not qualify** Golden facts, source identity, HWIR/RTL or execution authority.

This package changes only its owned test, standalone R0-01 runner, workflow and qualification documentation. It does not modify shared HB-INT-06 workflows, Golden source, Core consumer or external roadmaps.

## Baseline evidence and current dependency

* Reviewed original audit head: `8d3453dcc02bfb63710727f4f602edd9bc31efad`; re-read master before this package: `9befee4f3971b4945c553cf3162ed84af94134e4`.
* [HB-INT-06 Windows build job](https://github.com/yuriyyak23/HybridCPU-Compilers/actions/runs/44740086741/job/136301263920) (2026-11-20) recorded **companion-restore Passed exit=0**, **companion-build Failed exit=1** (stdout SHA256 `5AFEBCB2B9561FA38F3E9301FAE30109EBDCCFB7D14C703F7BC6AB57E0B48C2E`).
* Error examples: `CompilerProductionProviderContracts.cs(3,34) CS0234` unknown `HybridCPU.Compiler.Core.IR.Artifacts`; `CompilerProductionLoweringGate.cs(101,12) CS0246` `CompilerEmissionPackage`; `CompilerRuntimeBridge.cs(56,43) CS0246` `CompilerSidebandEnvelope`; plus `DescriptorEnvelope`, `TypedSlotFactsEnvelope`, `CompilerEvidenceEnvelope`, `SidebandRequirement`, `DescriptorAbiStatus`.
* The current master GitHub tree has `Core/IR` but **no** tracked `Core/IR/Artifacts/**`. Its file/directory commit-path history lookup returns no tracked files for that path. Searches reveal many consumers but no verified declarations of the required missing types. Do **not** invent blank types, trim the dependent graph or silently exclude the production code. This is an unresolved real production-source dependency, not proof that a `Compile Include` is the remedy.
* `Core/HybridCPU.Compiler.Core.csproj` has default SDK compile items and `Compile Remove` for `obj` and `$(BaseIntermediateOutputPath)`; `Cil` project references Core, `Roslyn` references Cil, and Companion references Roslyn. The new runner records the actual evaluated Compile/ProjectReference items in CI to distinguish this source-state diagnosis from an evaluated MSBuild exclusion.
* There is **no root global.json** in HybridCPU-Compilers. The Windows lane deliberately pins the historic separately reviewed **Core-compatible** SDK `11.0.100-rc.2.26504.105`; the toy observer's `11.0.100` is a distinct toolchain and must not be conflated with it. The lane verifies installed SDK, dotnet binary SHA256 and SDK Roslyn `csc.dll` SHA256 at runtime.

## Reproducible execution

The only CI entry point is `.github/workflows/hb-r0-companion-qualification.yml` on Windows 2025 for this package's paths. Checkout is an exact event commit with credentials removed. The script is:
`Compilers/HybridCPU_Compiler/HardwareBridge/Tests/R0_01_Companion_Qualification.ps1`.

With a matching SDK and clean checkout, execute on Windows PowerShell:

```powershell
$sha = (git rev-parse HEAD).Trim()
$temp = Join-Path $env:TEMP 'hb-r0-01-owned'
& ./Compilers/HybridCPU_Compiler/HardwareBridge/Tests/R0_01_Companion_Qualification.ps1 `
    -ExpectedSha $sha -ExpectedSdk '11.0.100-rc.2.26504.105' -OutputRoot $temp
```

The CI runner additionally requires `OutputRoot` inside `RUNNER_TEMP` (set `$env:RUNNER_TEMP` to a reviewed external isolated TEMP directory for manual runs). Its real commands are the equivalent of:

```powershell
dotnet --info
dotnet msbuild <CORE.csproj> -getItem:Compile -getItem:ProjectReference -getProperty:TargetFramework,BaseIntermediateOutputPath,OutputPath,DefineConstants,ArtifactsPath -p:UseArtifactsOutput=true -p:ArtifactsPath=<TEMP/artifacts-Debug> -p:Configuration=Debug
dotnet restore <COMPANION.csproj> -p:UseArtifactsOutput=true -p:ArtifactsPath=<TEMP/artifacts-Debug> -p:RestorePackagesPath=<TEMP/packages>
dotnet build <COMPANION.csproj> --no-restore -c Debug -p:UseArtifactsOutput=true -p:ArtifactsPath=<TEMP/artifacts-Debug> -p:RestorePackagesPath=<TEMP/packages>
# The same project graph is independently restored and built in Release with a different TEMP root.
# After a successful Companion build, build and execute the existing real HardwareBridge.SelfTests project.
```

The `HardwareBridge/Directory.Build.props` respects `ArtifactsPath`; `UseArtifactsOutput=true` propagates SDK artifact isolation across referenced production projects. The runner forbids an output root under the checkout, validates the evaluated graph before restore, and checks the real `HardwareBridge.Companion.dll` path and SHA. The compiled real-assembly test also opens PE metadata for the five Core-owned types, plus Cil/Roslyn/Companion, rejecting empty, synthetic, wrong-assembly and missing type definitions. **The test cannot run if a dependency fails to compile.**

Tracked Git file SHA256 inventories, HEAD and porcelain status are compared before and after the attempt; a changed/missing source produces `NoSourceWrite=Failed`. Receipts are created once in runner TEMP, not committed. Only the compact allowlisted `r0-01-qualification.json` is uploaded; stdout/stderr logs, compiled DLLs, PDBs, source and cache stay in runner TEMP. Each executed command has exit code, elapsed milliseconds and stdout/stderr SHA256. Skipped stages are `NotRun`, never `Passed`.

## Negative / mutation matrix

| Test | Expected result | Owner |
|---|---|---|
| Wrong .NET SDK or wrong HEAD SHA | Refuse before build | R0-01 |
| Modified tracked source or dirty checkout | Refuse / NoSourceWrite Failed | R0-01 |
| Output outside RUNNER_TEMP | Refuse | R0-01 |
| Missing Core/Cil/Roslyn ProjectReference edge | Refuse | R0-01 |
| Stale/generated Compile item in obj/bin/.artifacts | Refuse | R0-01 |
| Production Core-owned type absent | Real Csc compilation fails, never stubbed | External upstream source dependency |
| Missing actual DLL, invalid PE, public type mismatch | Reject, no synthetic PASS | R0-01 |
| NuGet restore fails or a build exits nonzero | Failed/Blocked with command receipt | R0-01 |
| Changed DefineConstants | Record exact effective evaluated graph; baseline must not be reused to qualify a changed variant | R0-01 |
| Actual Golden source-no-write | NotRun: no Golden checkout in this scoped lane | Requires separate approval |

These negative checks do not claim full independent Golden method provenance or hardware verification. An absent real Core-owned production source must be restored from a reviewed source revision and validated with genuine compiler tests before R0-01 can pass.

## First executed R0-01 CI receipts

At head `119aad1e5f8fc40e598b91ad61d78e015737eb92`, [R0-01 Windows run 44836199095](https://github.com/yuriyyak23/HybridCPU-Compilers/actions/runs/44836199095) produced a real uploaded [allowlisted receipt](https://github.com/yuriyyak23/HybridCPU-Compilers/actions/runs/44836199095/artifacts/14677974017):

* Effective .NET SDK: `11.0.100-rc.2.26504.105`. Dotnet executable SHA256: `0a1b7ca731245de1f267a9c3c4d1ef8705e0821923fcdd6e0d61c31132f51240`; SDK Roslyn `csc.dll` SHA256: `03a90c0454a9320dc853796f40fb113f5d6bbbb4b0b0eee6ae012551dd056255`.
* Actual evaluated Compile items: Core **294**, Cil **47**, Roslyn **5**, Companion **11**, for both Debug and Release.
* Debug restore exit **0** (stdout SHA256 `12b6ba7fe1ad320ead6fa6f25aef6b470472a98af84183ca3a7f13ea843be767`), build exit **1** (stdout SHA256 `d2c69d1c7b494c0323b4bb43951d529ac2defbeb4fc3ea358d2d2f485b61956a`).
* Release restore exit **0** (stdout SHA256 `0c333aa2c6bd97479fd9f7fe1bba74763bab1c88696f17fab80474872ea8b474`), build exit **1** (stdout SHA256 `6f934438b5bdfa5bd79b890a68db34ead0fce6f0b149f2a1c59974439f22d815`).
* All four command stderr SHA256 were `e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855` (empty). The five guarded negative probes were Rejected.
* Full tracked source SHA inventory: **2082 files**, before/after identical SHA256 `6c2444c6768e008307c6fdc36bd2efb7bdca93337276c663b5a96acf9bf805e2`; `NoSourceWrite=Passed`.
* Actual real Companion PE/PDB and type reflection tests: **NotRun**, because both real builds failed. Qualification remains **Blocked**.

These are facts for the stated SHA only, not a substitute for a later successful current-head CI run. The later runner hardening also checks evaluated output paths under TEMP.

## Acceptance and status semantics

`Passed` requires clean **Debug AND Release** production Companion PE outputs, real PE/type contract tests, identical tracked source SHA256 inventory and a valid immutable receipt uploaded by successful GitHub CI at the reviewed head commit.

Until that evidence exists, the qualification is **Blocked** (with actual `Build=Failed` recorded for failures). The upstream missing tracked `Core/IR/Artifacts` production code is the current blocker; do not interpret restore success or historical fixture-only successes as completion. A Core private read token missing in a separate HB-INT-06 job is **outside this R0-01 scope**. Do not merge this Draft PR solely because the receipt has been published.

## Post-merge residual R0-01 qualification (2026-11-21)

**This section supersedes earlier status wording for the current execution baseline; historical receipts are not retroactively relabeled.** After PR #28 merged, the Bridge `master` advanced to `d243dd7ecce243cc08a13303cf01247ba7a65024` (one commit after `b06cd1d42e88d53f35bd8054e06fcfab6f07bff4`). Private **CircuitWeave** `master` was independently rechecked at `b5c44685197a40df99a4d48891120b12475c6ca5`; it is *not* the `HybridCPU.Compiler.Core` production project and is not built in this lane. No open Bridge PRs were found at this check.

* [Current master R0-01 Windows run 44848718719](https://github.com/yuriyyak23/HybridCPU-Compilers/actions/runs/44848718719), [job 136674710805](https://github.com/yuriyyak23/HybridCPU-Compilers/actions/runs/44848718719/job/136674710805), concluded **failure** (runner exit `1`). The emitted log says `qualification=Blocked; no-source-write=Passed`. [Restricted receipt artifact 14683693640](https://github.com/yuriyyak23/HybridCPU-Compilers/actions/runs/44848718719/artifacts/14683693640) is evidence of a blocked run, **not** a successful build.
* Fresh recursive tracked tree inspection confirmed **zero** files below `Compilers/HybridCPU_Compiler/Core/IR/Artifacts/**` on that SHA. The API commit history for the path was empty. Do not conclude the remedy is solely a `Compile Include`: the Core project still uses SDK Compile defaults, removes `obj/**` and intermediate output paths only, and has consumers of the unavailable namespace in `Core/IR/Authority/CompilerProductionLoweringGate.cs` and `Core/IR/Lowering/Production/CompilerProductionProviderContracts.cs`.
* External software-compiler/Core IR owner must provide **authentic** declarations for `CompilerEmissionPackage`, `CompilerSidebandEnvelope`, `DescriptorEnvelope`, `TypedSlotFactsEnvelope`, `CompilerEvidenceEnvelope`, `SidebandRequirement`, `DescriptorAbiStatus`, their transitive contracts and semantics. The owner must determine the authoritative source path/revision and publish a **separate** compiler-owned PR; [dependency issue #38](https://github.com/yuriyyak23/HybridCPU-Compilers/issues/38). The first previously evidenced failure was `CS0234` in the actual Core compiler invocation, followed by `CS0246`, not a Bridge-file deficiency.
* Historical standalone R0-01 evidence `44836199095` recorded Debug/Release restore exit `0`, build exit `1`, and unchanged tracked source inventory, **not** real Companion PE. The new sequential build and PE tests are **NotRun** until a new exact-head CI receipt exists.

### Follow-up implementation, confined to R0-01 ownership

1. The runner now explicitly attempts real `Core → Cil → Roslyn → Companion` builds with the existing production project references and per-configuration TEMP artifacts, after the genuine Companion-closure restore. It records the first failing project and executes Release independently even if Debug fails.
2. The new `hybridcpu.hardwarebridge.r0-01-companion-build-receipt/v2` is a **new** schema ID: old v1 receipts remain immutable and are not reinterpreted. Each configuration records `ProjectBuilds`, the actual first failed project, assembly and (when produced) PDB hashes, and `FailureKind`: `SourceDependencyMissing`, `BuildFailed`, `RealPEMissing`, `Passed` (or `NotRun` for skipped work). `SourceDependencyMissing` requires **both** absence of tracked artifacts source and matching **executed** Core compiler diagnostics; absence of a directory alone does not prove that a particular build failed for that reason.
3. A successful build alone is insufficient. The selftest reopens actual managed PEs and validates assembly/module metadata, MVID and public Core-owned type definitions. It rejects a swapped valid DLL, wrong module name, wrong type origin, and a fake on-disk PE. The runner binds each assembly to its own evaluated output directory, verifies SHA before/after execution, and never reports `PeCheck=Passed` unless the real selftest passes.
4. Output roots must be new, empty, within `RUNNER_TEMP`; pre-existing caches and reparse ancestors are refused. Evaluated Debug/Release Compile closures must match. `Git HEAD`, porcelain, tracked inventory and SHA checks remain mandatory. No original Golden checkout, no private CircuitWeave fetch, and no source/project/targets changes outside the four R0-01 files.
5. R0-01 workflow triggers cover the **entire actual compiler input closure**, not only `Core/IR/Artifacts/**`, so upstream compiler-owner fixes request a fresh qualification.

**Status:** Guard code `Implemented` on the feature branch. Until a fresh exact-head run proves all four real production modules in both configurations and selftests, `TestedInCI = NotRun` for these changes, `ProductionBuild = Blocked`, `Golden = NotRun`, `Paired Core = NotRun`, `RTL = NotRun`. No missing declarations are fabricated. Existing public diagnostics are summarized; full stdout/stderr and binaries remain TEMP-only.
