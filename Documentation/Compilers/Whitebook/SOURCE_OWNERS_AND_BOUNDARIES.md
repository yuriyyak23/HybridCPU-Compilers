# Source owners, compilation graph and dependency boundaries

**Whitebook appendix · reviewed revision:** `06700ff9a7c650be60ea6a85783f29acdd4f2b9b` (2026-11-11). **Purpose:** show actual compiler ownership without assuming that one solution build or a shared data structure qualifies all frontends. Return to [Whitebook](README.md).

## 1. Actual repository layout

```text
HybridCPU-Compilers/
├── Compilers/
│   ├── HybridCPU_Platform.Contracts/  # immutable machine / ABI contracts
│   ├── HybridCPU_Compiler/
│   │   ├── Core/                     # canonical IR, scheduling, bundling, lowering
│   │   ├── Native/                   # native ASM / carrier frontend
│   │   ├── LLVM/                     # bounded LLVM importer / mapping
│   │   ├── Cil/                      # restricted CIL, exact body/world ownership
│   │   ├── Roslyn/                   # explicit C# source companion + CLI
│   │   ├── Analyzers/               # KernelNoHeapAnalyzer
│   │   ├── Generators/              # static declarations / contracts
│   │   ├── NativeAot/               # patched ILCompiler and HCO/link/HCEXE adapter
│   │   ├── Oracle/                  # exact scheduling oracle
│   │   ├── Profiles/                # target/profile contracts
│   │   ├── ReleaseQualification/    # outward-only scoped release manifests
│   │   ├── HardwareBridge/          # opt-in unqualified read-only bridge
│   │   └── Tools/                   # compiler/profile/managed-port smoke
│   ├── HybridCPU_RuntimeKernel/
│   ├── HybridCPU_ManagedRuntime/
│   ├── CpuInterfaceBridge/
│   └── ConsoleNetCompiler/
├── Diagnostics/
├── Tests/
├── corpus/
├── Documentation/Compilers/Whitebook/
├── HybridCPU Compilers.slnx
└── LICENSE.txt
```

The top-level [`HybridCPU Compilers.slnx`](../../../HybridCPU%20Compilers.slnx) groups many independent projects. The two **opt-in** HardwareBridge projects are in their own subtree and are **not listed as projects** in this solution: [companion](../../../Compilers/HybridCPU_Compiler/HardwareBridge/HardwareBridge.Companion.csproj) and [self-tests](../../../Compilers/HybridCPU_Compiler/HardwareBridge/Tests/HardwareBridge.SelfTests.csproj).

## 2. Canonical Core

| Owner | Exact source | Ownership/contract |
|---|---|---|
| Public Core compiler facade | [HybridCpuCanonicalCompiler.cs](../../../Compilers/HybridCPU_Compiler/Core/Compilation/HybridCpuCanonicalCompiler.cs) | Stateless canonical instruction program pipeline |
| Canonical IR | [IR Model](../../../Compilers/HybridCPU_Compiler/Core/IR/Model/) | Operands, functions, program/CFG, versioned compiler contracts |
| Target machine | [HybridCpuMachineDescriptionV1.cs](../../../Compilers/HybridCPU_Compiler/Core/IR/Resources/HybridCpuMachineDescriptionV1.cs) | W8 slot topology, deterministic structural facts |
| Dependencies and hazards | [IR/Hazards](../../../Compilers/HybridCPU_Compiler/Core/IR/Hazards/) | Data, memory, control and resource conflicts |
| Scheduling | [IR/Scheduling](../../../Compilers/HybridCPU_Compiler/Core/IR/Scheduling/) | Local, region, modulo, topology/profile gated heuristics |
| Bundling | [HybridCpuProgramBundler.cs](../../../Compilers/HybridCPU_Compiler/Core/IR/Compilation/HybridCpuProgramBundler.cs) | Typed-slot placement and capacity |
| Register allocation | [HybridCpuScheduleAwareRegisterAllocatorV1.cs](../../../Compilers/HybridCPU_Compiler/Core/IR/Allocation/HybridCpuScheduleAwareRegisterAllocatorV1.cs) | Optional, bounded, final-witness aware |
| Post-bundle | [HybridCpuPostBundleCompiler.cs](../../../Compilers/HybridCPU_Compiler/Core/IR/Compilation/HybridCpuPostBundleCompiler.cs) | Lowering, relocations, annotations, serialization |
| Compiler authority | [IR/Authority](../../../Compilers/HybridCPU_Compiler/Core/IR/Authority/) | Separation between diagnostic facts, admission and production emission |
| Telemetry/profile | [IR/Telemetry](../../../Compilers/HybridCPU_Compiler/Core/IR/Telemetry/) | Explicit opt-in/advisory data, not hidden target selection |

Core project: [`HybridCPU.Compiler.Core.csproj`](../../../Compilers/HybridCPU_Compiler/Core/HybridCPU.Compiler.Core.csproj) references [`HybridCPU.Platform.Contracts`](../../../Compilers/HybridCPU_Platform.Contracts/HybridCPU.Platform.Contracts.csproj) and does **not** reference upper ISE, LLVMSharp, Roslyn or NativeAOT. Do not collapse these ownership boundaries to optimize a shared build.

## 3. Frontends and producer/consumer rules

| Input | Project / public source | Boundary |
|---|---|---|
| Native ASM/carrier | [Native project](../../../Compilers/HybridCPU_Compiler/Native/HybridCPU.Compiler.Native.csproj), [frontends](../../../Compilers/HybridCPU_Compiler/Native/NativeCompilerFrontends.cs) | Native compiler owns parsed words, target/build provenance, refusal |
| LLVM modules | [LLVM project](../../../Compilers/HybridCPU_Compiler/LLVM/HybridCPU.Compiler.Llvm.csproj), [importer](../../../Compilers/HybridCPU_Compiler/LLVM/LlvmModuleImporterV1.cs) | LLVMSharp 22.1.8 dependency; semantic mapping bounded/default-off |
| C# owned sources | [Roslyn project](../../../Compilers/HybridCPU_Compiler/Roslyn/HybridCPU.Compiler.Roslyn.csproj), [Companion](../../../Compilers/HybridCPU_Compiler/Roslyn/ManagedSourceCompanionV1.cs) | C# 13 default via Roslyn 4.14.0, C# 15 explicit gated SDK-host; diagnostics/NoHeap |
| Managed PE/CIL | [CIL project](../../../Compilers/HybridCPU_Compiler/Cil/HybridCPU.Compiler.Cil.csproj), [importer](../../../Compilers/HybridCPU_Compiler/Cil/RestrictedCilImporterV1.cs) | Exact metadata/body ownership, restricted IL/managed semantics |
| ILCompiler graph | [NativeAOT adapter](../../../Compilers/HybridCPU_Compiler/NativeAot/HybridCPU.Compiler.NativeAot.Adapter.csproj), [target map](../../../Compilers/HybridCPU_Compiler/NativeAot/PHASE23_TARGET_ENABLEMENT_MAP.md) | Pinned external patched upstream .NET 10 ILCompiler, no general CLR target |
| Hardware facts | [HardwareBridge companion](../../../Compilers/HybridCPU_Compiler/HardwareBridge/HardwareBridge.Companion.csproj) | Opt-in Roslyn/CIL facts; no HWIR/RTL/qualification |

Direction of dependencies: caller/frontend → (possibly CIL/Roslyn) → shared Core → Platform.Contracts. RuntimeKernel/ManagedRuntime hold *runtime component* contracts; external ISE owns actual CPU execution. Release qualification consumes versioned component outputs outwardly, not by making Core depend on release code.

## 4. Identity matrix

| Identity | Defined or projected by | Scope — do not confuse with |
|---|---|---|
| Machine contract version/digest | [machine description](../../../Compilers/HybridCPU_Compiler/Core/IR/Resources/HybridCpuMachineDescriptionV1.cs), [contract v6](../../../Compilers/HybridCPU_Compiler/Core/IR/Model/HybridCpuCompilerContracts.cs) | Runtime permission / live machine |
| Source input identity | [ManagedSourceCompanion](../../../Compilers/HybridCPU_Compiler/Roslyn/ManagedSourceCompanionV1.cs) | PE/PDB output or call graph identity |
| PE/PDB artifact identity | [CompanionArtifactIdentityV1](../../../Compilers/HybridCPU_Compiler/Roslyn/CompanionArtifactIdentityV1.cs) | Dependency module's PDB |
| Exact method/graph identity | [CIL importer](../../../Compilers/HybridCPU_Compiler/Cil/RestrictedCilImporterV1.cs) | Method name only, signature guess, arbitrary metadata references |
| Source map | [ManagedSourceMapV1](../../../Compilers/HybridCPU_Compiler/Cil/ManagedSourceMapV1.cs) | Proof that arbitrary reference methods have primary source |
| Bridge snapshot digest | [BridgeSourceSnapshotV1](../../../Compilers/HybridCPU_Compiler/HardwareBridge/SourceSnapshot/BridgeSourceSnapshotV1.cs) | Companion SourceInputDigest / Hardware BindingLock |
| Bridge sidecar payload hash | [BridgeSidecarCodecV1](../../../Compilers/HybridCPU_Compiler/HardwareBridge/SourceSnapshot/BridgeSidecarCodecV1.cs) | Signature, Golden attestation or verified RTL |
| NativeAOT patch/pack identity | [Phase 23](../../../Compilers/HybridCPU_Compiler/NativeAot/PHASE23_TARGET_ENABLEMENT_MAP.md), [Phase 26](../../../Compilers/HybridCPU_Compiler/NativeAot/Packaging/PHASE26_SDK_PACK_V1.md) | Generic host SDK version or runtime target legality |
| Release/evidence manifest | [ReleaseQualification](../../../Compilers/HybridCPU_Compiler/ReleaseQualification/README.md) | Actual two independent clean builds or guest execution |

## 5. Source-owned build integration and potential traps

1. The upper [`HybridCPU_Compiler.csproj`](../../../Compilers/HybridCPU_Compiler/HybridCPU_Compiler.csproj) excludes nested `Core/`, `Cil/`, `Roslyn/`, `Native/` and other compiler projects from recursive compilation and references them according to its own project graph. It **does** reference an external ISE project using `../../../HybridCPU ISE/HybridCPU_ISE/HybridCPU_ISE.csproj` relative to the upper csproj.
2. The [root tests](../../../Tests/HybridCPU.Compiler.Tests.csproj) also reference the sibling ISE and even link selected ISE test helpers. A standalone source checkout alone does not establish that this project builds.
3. The HardwareBridge typed projection has a conditional source gate `HARDWAREBRIDGE_COMPANION` and a separate opt-in [project](../../../Compilers/HybridCPU_Compiler/HardwareBridge/HardwareBridge.Companion.csproj) with explicit `Compile` includes. It is **not automatically an API available in every consumer of the upper project**.
4. [`Directory.Build.props` inside HardwareBridge](../../../Compilers/HybridCPU_Compiler/HardwareBridge/Directory.Build.props) redirects outputs to prevent default recursive parent compilation from ingesting generated source metadata.
5. Root solution lists numerous projects but no universal execution environment; per-project evaluated MSBuild properties, package closure and target tools determine reproducible success.
6. There is no root `global.json` file in the inspected tree. Read actual `dotnet --info` and use exact tested .NET 11 compiler SDK; do **not** confuse it with the older pinned patched ILCompiler or SDK pack.

## 6. Engineering rule

Before changing any layer, identify **the source owner, exact consumer, input authority, negative-refusal behavior and minimum test that actually executes that path**. Existence of a class, schema, manifest, bundle or successful parser result is not authority for the next stage. Frontend-specific ABI/effects, runtime legality and real hardware verification are separate concerns.

See [build and qualification](BUILD_TEST_AND_RELEASE.md) and [HardwareBridge boundary](HARDWAREBRIDGE_AND_CIRCUITWEAVE.md).
