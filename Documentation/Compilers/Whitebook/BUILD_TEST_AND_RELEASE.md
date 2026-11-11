# Build, tests, reproducibility and release evidence

**Whitebook appendix, source audit 2026-11-11.** This is an **execution guide and proof-scope matrix**, not a report claiming successful tests in the current environment. Reviewed source revision: `06700ff9a7c650be60ea6a85783f29acdd4f2b9b`. [Whitebook](README.md) · [Source owners](SOURCE_OWNERS_AND_BOUNDARIES.md).

## 1. Toolchain identities are separate

| Identity | Source | Meaning |
|---|---|---|
| Compiler host | [Core project](../../../Compilers/HybridCPU_Compiler/Core/HybridCPU.Compiler.Core.csproj), [Roslyn project](../../../Compilers/HybridCPU_Compiler/Roslyn/HybridCPU.Compiler.Roslyn.csproj) | The maintained compiler assemblies target `net11.0` |
| Default Roslyn | [Roslyn project](../../../Compilers/HybridCPU_Compiler/Roslyn/HybridCPU.Compiler.Roslyn.csproj) | `Microsoft.CodeAnalysis.CSharp 4.14.0` with `LangVersion 13.0` and default C# 13 source lane |
| C# 15 opt-in host | [SDK-Roslyn targets](../../../Compilers/HybridCPU_Compiler/Roslyn/SdkRoslynHost.targets) | `UseSdkRoslynForCompanion=true`: both source consumer and companion must use matching SDK compiler identity |
| LLVM frontend | [LLVM project](../../../Compilers/HybridCPU_Compiler/LLVM/HybridCPU.Compiler.Llvm.csproj) | LLVMSharp/LLVMSharp.Interop 22.1.8; optional/native runtime version/ABI requires qualification |
| Test framework | [xUnit tests](../../../Tests/HybridCPU.Compiler.Tests.csproj) | Microsoft.NET.Test.Sdk 18.11.0, xunit 2.9.3, runner 4.0.1 for this repo source snapshot |
| Patched NativeAOT ILCompiler | [Phase 23 target map](../../../Compilers/HybridCPU_Compiler/NativeAot/PHASE23_TARGET_ENABLEMENT_MAP.md) | Separate pinned `dotnet/runtime` commit / .NET 10.0.105 host and patch-set identity |
| Restricted publish pack | [Phase 26 SDK pack](../../../Compilers/HybridCPU_Compiler/NativeAot/Packaging/PHASE26_SDK_PACK_V1.md) | Independent publish SDK/host/target RID, runtime pack and installed ILCompiler binaries |

**No repository-root `global.json`** was found in this repository's current tracked tree. If an external workspace includes one, record the exact evaluated SDK and its provenance; never silently impose a historical NativeAOT pin on all `net11.0` projects.

## 2. Repository layout for integrated builds

Some project files explicitly reference a **sibling ISE checkout** outside `HybridCPU-Compilers`:

```text
workspace/
├── HybridCPU-Compilers/
│   └── Compilers/HybridCPU_Compiler/HybridCPU_Compiler.csproj
└── HybridCPU ISE/
    └── HybridCPU_ISE/
        └── HybridCPU_ISE.csproj
```

The upper [compiler project](../../../Compilers/HybridCPU_Compiler/HybridCPU_Compiler.csproj) uses relative `../../../HybridCPU ISE/HybridCPU_ISE/HybridCPU_ISE.csproj`. [Root test project](../../../Tests/HybridCPU.Compiler.Tests.csproj) also references ISE and linked test sources. Those **cannot be assumed buildable** from a standalone clone containing only this repository. See [source boundaries](SOURCE_OWNERS_AND_BOUNDARIES.md). Do not create a dummy ISE or alter a project reference just to get a green signal.

## 3. First-pass isolated build checks

From the repository root:

```bash
dotnet --info

# Minimal independent machine/IR compilation:
dotnet build Compilers/HybridCPU_Compiler/Core/HybridCPU.Compiler.Core.csproj -c Release

# Restricted IL and explicit Roslyn companion:
dotnet build Compilers/HybridCPU_Compiler/Cil/HybridCPU.Compiler.Cil.csproj -c Release
dotnet build Compilers/HybridCPU_Compiler/Roslyn/HybridCPU.Compiler.Roslyn.csproj -c Release

# Opt-in read-only HardwareBridge:
dotnet build Compilers/HybridCPU_Compiler/HardwareBridge/HardwareBridge.Companion.csproj -c Release
dotnet run --project Compilers/HybridCPU_Compiler/HardwareBridge/Tests/HardwareBridge.SelfTests.csproj -c Release
```

Use actual CLI exit codes and logs. If the installed SDK is not suitable, a dependency cannot be restored, or a required native library is absent, mark the corresponding entry `NOT RUN / BLOCKED`; **do not** report success. An isolated Core build does not imply an image, emulator or full solution build.

The HardwareBridge self-tests require a compatible SDK/runtime reference and use an explicit compile-only Roslyn/CIL fixture. They do not execute emitted guest PE or RTL. See [HardwareBridge self-tests](../../../Compilers/HybridCPU_Compiler/HardwareBridge/Tests/README.md).

## 4. Roslyn CLI behavior and error handling

The command-line entry point is [`ManagedSourceCommandLineV1`](../../../Compilers/HybridCPU_Compiler/Roslyn/ManagedSourceCommandLineV1.cs) in an executable Roslyn project. A **schematic** example that requires an existing compatible source and reference set:

```bash
dotnet run --project Compilers/HybridCPU_Compiler/Roslyn/HybridCPU.Compiler.Roslyn.csproj -c Release -- \
  compile-source-image \
  --source path/to/Program.cs \
  --reference path/to/required-runtime-reference.dll \
  --assembly-name Example \
  --type Example \
  --method Read \
  --out output/Example.hcexe
```

Explicit inputs must be complete and byte-bound. Repeat `--source` and `--reference` as needed; `--body-reference` selects a code-bearing dependency and `--metadata-reference` grants declarations only. Optional exact generic contexts are selected using `--type-argument int32|int64` and `--method-argument int32|int64` within the documented limits. `--emit-pe` / `--emit-pdb` / `--emit-map` are optional additional outputs. Do not assume a caller-supplied reference or method is compatible; the compiler must enforce its own admission.

[The Roslyn README](../../../Compilers/HybridCPU_Compiler/Roslyn/README.md) documents currently assigned exit classes: usage/input IO `2`, source/NoHeap rejection `3`, graph rejection `4`, link/image refusal `5` and caller cancellation `130`. Successful inputs must be checked against actual emitted artifacts and independent scope-specific controls. Multiple optional output writes are not promised atomic as a group.

## 5. Test and regression matrix

| Layer | Test owner | Required checks | What a PASS does not prove |
|---|---|---|---|
| Core/native | [Tests/CompilerTests](../../../Tests/CompilerTests/), [exact oracle](../../../Compilers/HybridCPU_Compiler/Oracle/HybridCpuEnumerativeExactOracleV1.cs) | W8 slot hazards, dependency, scheduling, bundling, relocation, repeat and refusal | Full target runtime execution |
| Roslyn/NoHeap | [CompilerProfileSmoke](../../../Compilers/HybridCPU_Compiler/Tools/CompilerProfileSmoke/), [Roslyn docs](../../../Compilers/HybridCPU_Compiler/Roslyn/README.md) | Source diagnostics, forbidden effects, language host, reference roles, deterministic PE/PDB | Arbitrary C# or BCL support |
| Managed CIL/ABI | [ManagedPortSmoke](../../../Compilers/HybridCPU_Compiler/Tools/ManagedPortSmoke/README.md) | Exact token/definition/generics, CFG/SSA, helper effects, EH/GC/ABI and negative body closure cases | Full managed runtime publication |
| NativeAOT object/image | [Phase 24](../../../Compilers/HybridCPU_Compiler/NativeAot/PHASE24_OBJECT_FORMAT_HCO_V1.md), [restricted execution](../../../Compilers/HybridCPU_Compiler/NativeAot/PHASE24_RUNNABLE_RESTRICTED_IMAGE_V1.md) | HCO checksum/symbol/relocation, HCEXE inspect/startup, target/pin/refusal | All host-compiled C# programs run in guest |
| SDK pack / demo | [Phase 26](../../../Compilers/HybridCPU_Compiler/NativeAot/Packaging/PHASE26_SDK_PACK_V1.md), [AOT demo](../../../Diagnostics/HybridCpuAotAddDemo/README.md) | Exact external ILCompiler/SDK/patch/pack/RID, restricted corpus and provenance | General native AOT support |
| HardwareBridge | [Bridge self-tests](../../../Compilers/HybridCPU_Compiler/HardwareBridge/Tests/README.md) | Source defensive copy, exact snapshot/body roles, sidecar checksum, scalar observations, negative cases | Hardware bindings, RTL equivalence |
| Release | [Release owner](../../../Compilers/HybridCPU_Compiler/ReleaseQualification/README.md) | Exact profiles, source/toolchain/options identities and separately attested builds | Dynamic execution, runtime permission, independent builds not performed by validator |

**Commands are not interchangeable.** `CompilerProfileSmoke` and `ManagedPortSmoke` expose test groups and may require explicit switches and fixtures; a bare `dotnet run` is not necessarily the complete suite. Select exact cases from their current source/README, preserve output and failure logs, and state *which* campaign was actually run. For the test project with sibling ISE present:

```bash
dotnet test Tests/HybridCPU.Compiler.Tests.csproj -c Release
```

This command is a **proposed verification action**, not a statement that it succeeded in this documentation update.

## 6. Reproducible artifacts and negative controls

A trustworthy compiler experiment records:
1. exact repository SHA, local modifications, host OS/architecture, SDK, Roslyn/LLVM/ILCompiler, native/runtime packs and referenced source closure;
2. input bytes/hashes, selected roots, body versus metadata-only reference roles, target/machine contracts, options and profile;
3. invocation arguments, outputs, actual exit codes, cancellations and time/resource budgets;
4. PE/PDB/source-map and graph identities separately from HCO/HCEXE and sidecar byte digests;
5. independent semantic/physical checks, not just serialization equality;
6. **negative cases**: malformed or stale identity, unsupported opcode, wrong slot, unknown helper effect, ambiguous type/generic selector, absent implementation-bearing body, output corruption, cancellation, changed SDK/tool version;
7. reproducible *fresh* run directories and retention of all failure attempts.

Digest equality can establish byte-for-byte equality under a defined hashing rule. It does not prove that two builds were independently clean, that a source method matches hardware or that a guest executed correctly.

## 7. Release profile taxonomy and claims

The current [Phase 27 contract](../../../Compilers/HybridCPU_Compiler/ReleaseQualification/PHASE27_RELEASE_V1.md) explicitly distinguishes:

| Profile | Source-level release disposition | Gaps |
|---|---|---|
| `HybridCPU.Native` | Scoped release-authorized ASM/carrier profile | No automatic runtime legality or general object format |
| `HybridCPU.LLVM` | Optional/default-off verified ingress | No scheduler/backend ownership, no universal LLVM coverage |
| `HybridCPU.DotNetAot.Restricted` | Scoped release-authorized single-static-method/value-only custom RID pack | Only exact tested host/RID/pinned SDK and component subset |
| `HybridCPU.DotNetAot.Managed` | Unsupported end-to-end | Host component tests do not establish all GC/EH/runtime semantics |
| `HybridCPU.Full` | Unsupported umbrella profile | No full CPU/runtime/OS support inferred |

The term `release-authorized` above belongs to the **source-defined manifest profile**, not a new external clean-build or production shipment claim.

[ReleaseQualification ownership](../../../Compilers/HybridCPU_Compiler/ReleaseQualification/README.md) explains that the `write-refplan7-final` path **declares** two clean build identities; it does not execute the builds. A passing validator checking two equal image hashes cannot certify independent construction. A future real producer must retain exact isolated build roots, commands, binaries, exits and hashes and bind them in an independently checked receipt. No build/release claims are promoted by this Whitebook PR.

For the scoped release verifier, consult [`Verify-Phase27Release.ps1`](../../../Compilers/HybridCPU_Compiler/ReleaseQualification/Verify-Phase27Release.ps1) and supply exact required source/evidence identifiers — do not execute it with invented hashes.

## 8. HardwareBoundary and external verification

The optional [HardwareBridge](HARDWAREBRIDGE_AND_CIRCUITWEAVE.md) produces unqualified software source/facts/Golden observations; it has no authority over C#→HWIR→RTL, IP or formal results. [CircuitWeave](https://github.com/yuriyyak23/CircuitWeave) owns independently validated Hardware Bindings, hardware semantics and verification ladder. [CircuitWeave-Studio](https://github.com/yuriyyak23/CircuitWeave-Studio) is a read-only evidence viewer. An RTL tool process exit `0`, matching Golden vectors or a pretty waveform is insufficient to declare source-to-hardware equivalence.

## 9. Documentation-only update scope

This Whitebook/README revision does **not** execute .NET, NativeAOT, a guest CPU or an EDA tool; it audits source structure and documentation links. To qualify a new claim, rerun the appropriate supported profile, capture proof-of-execution artifacts and cite the **exact revision and run**. Historical reports are context, not live current results.
