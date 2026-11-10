# Phase 23A NativeAOT target-enablement map

## Pinned baseline and evidence identity

- source: `dotnet/dotnet@94ea82652cdd4e0f8046b5bd5becbd11461482ca` (the `Microsoft.DotNet.ILCompiler` 10.0.8 source-repository commit);
- host SDK: `10.0.105`, from the pinned VMR `global.json`;
- immutable source archive SHA-256: `694b9134d338db6b0d31b1a73d9c89e5419432f2f0622b610ecdf5455e851b96`;
- maintained patch: `Patches/0001-hybridcpu-phase23-single-method-seam.patch`;
- patch SHA-256: `e8c8169badfd8b70541c7ee733b384fdb41285b0652d7a6c94d427647d93e1b9`;
- clean upstream build: `build.cmd clr.aot+libs -rc Release -lc Release`, 0 errors / 0 warnings;
- patched managed ILCompiler build: `ILCompiler.csproj`, 0 errors / 0 warnings;
- spike: `RestrictedCilCSharpFixtures.Add(int,int)` traversed `SingleMethodRootProvider` and `DependencyAnalyzer.ComputeMarkedNodes`, produced 512 HybridCPU bytes, SHA-256 `e6de5039cdb89f272ff310786d7e91909f14cb8c3d3fc28ef936d76d11bf9e16`.

The source paths below are relative to the pinned runtime tree. Line numbers identify this exact commit; the file hashes at the end are the deterministic drift authority.

## 1. Target architecture, OS, and ABI identity

`src/coreclr/tools/Common/TypeSystem/Common/TargetArchitecture.cs` declares the closed architecture enum. `TargetDetails.cs` binds architecture, OS, ABI, pointer size, endianness, and vector size. `src/coreclr/tools/aot/ILCompiler/ILCompilerRootCommand.cs:162-177` parses the closed `--targetarch`, `--targetos`, and single-method surface; `src/coreclr/tools/Common/CommandLineHelpers.cs` rejects unknown architecture strings. `Program.cs:107-129` creates `InstructionSetSupport` and `TargetDetails`.

There is no `HybridCPU` identity in the pinned enum/parser. Adding a production target identity therefore requires a maintained fork and downstream runtime-pack work; Phase 23 deliberately does not mislabel x64 as HybridCPU. The spike uses x64 only as the ILCompiler type-system/dependency-graph bootstrap and bypasses host object emission completely.

## 2. Target details, instruction features, and codegen selection

`Program.cs:107-125` selects target details and ISA features. `Program.cs:362-373` hard-wires `RyuJitCompilationBuilder` and optionally a JIT library. `RyuJitCompilationBuilder.cs:146-165` constructs `RyuJitNodeFactory`, initializes `JitConfigProvider`, creates the dependency graph, and returns `RyuJitCompilation`.

The builder and compilation classes are sealed. There is no generic external target plug-in API. The patch recognizes a closed set of `HybridCpu*` backend options, removes them before JIT configuration, and activates an explicit single-method seam. Normal NativeAOT behavior is byte-for-byte on the unchanged branch.

## 3. Method compilation/codegen adaptation boundary

`ILCompiler.Compiler/Compiler/Compilation.cs:22-89,537-545` owns roots, IL provider, graph, and the `ICompilation.Compile` entry. `RyuJitCompilation.cs:100-120,122-230` marks the graph, discovers `MethodCodeNode` work, and normally calls `CorInfoImpl.CompileMethod`. `MethodCodeNode.cs:18-45,117-145,252-254` owns code, relocations, frame, GC, EH, and non-relocation dependency data.

The maintained hook is inside the actual `ComputeDependencyNodeDependencies` callback: it validates the graph-selected method, invokes the external adapter, and calls `MethodCodeNode.SetCode`. The external adapter receives only assembly path plus qualified type/method identity and returns frontend-neutral HybridCPU code and a versioned sidecar. `HybridCPU.Compiler.Core` never references `ILCompiler`, `Internal.TypeSystem`, or NativeAOT nodes.

## 4. Relocations, symbols, sections, and object emission

`src/coreclr/tools/Common/Compiler/DependencyAnalysis/ObjectDataBuilder.cs`, `Relocation.cs`, and `ObjectNodeSection.cs` define object fragments, relocations, symbols, and sections. `ILCompiler.Compiler/Compiler/ObjectWriter/ObjectWriter.cs` lowers marked object nodes to the host object writer. `RyuJitCompilation.cs:100-120` normally calls it after marking.

Phase 23 accepts only relocation-free restricted methods. The patch installs an empty relocation set on the method node and writes the exact Core bytes after marking instead of calling `ObjectWriter`. Object format, section layout, relocation lowering, and linking remain unsupported and are owned by Phase 24.

## 5. GC, unwind, EH, helpers, and method metadata

`MethodCodeNode.cs:117-145` exposes frame records, GC info, and EH info. `NodeFactory.cs:427-430,689-693,1284-1412` creates method entrypoints, ReadyToRun helpers, generic lookup helpers, and method metadata. `RyuJitCompilation.cs:194-230` normally lets `CorInfoImpl` populate these records.

The v1 seam contract carries explicit empty relocation/frame collections and nullable GC/EH fields. Any method needing managed references, EH, P/Invoke, runtime calls, or non-inline helpers is rejected by the Phase 21 closed support matrix before node data can be emitted. These features remain Phase 25 work.

## 6. Dependency roots, IL, and generic context

`Program.cs:190-206` creates `SingleMethodCompilationModuleGroup` and `SingleMethodRootProvider`. `Compilation.cs:40-74` attaches root providers to `RootingServiceProvider`; `Compilation.cs:78-85` supplies method IL. `NodeFactory.cs:1064-1183` owns method-entrypoint caches. `Compilation.cs:238-404` maps runtime and generic dictionary lookups. `RyuJitCompilation.cs:100-158` completes marking and enumerates graph-selected methods.

The spike preserves this root and marking path. It does not perform ad-hoc whole-program discovery. Generic definitions/instantiations and dictionary/context requests are explicitly rejected because the existing restricted CIL contract has not qualified them.

## 7. Runtime helpers, startup libraries, and runtime packs

`src/coreclr/nativeaot/BuildIntegration/Microsoft.DotNet.ILCompiler.SingleEntry.targets:20-28,43-71` derives target architecture and selects host ILCompiler plus `Microsoft.NETCore.App.Runtime.NativeAOT.<rid>`. `Microsoft.NETCore.Native.targets:134-142` resolves the runtime-pack inputs. `Microsoft.NETCore.Native.Windows.targets:24-115` selects bootstrapper, runtime, GC, compression, globalization, and system libraries. `Program.cs:253-278` adds startup/configuration/helper roots.

No HybridCPU runtime helper library or RID exists yet. The Phase 23 single-method gate excludes startup/runtime roots and permits only inline/no-runtime-call helpers. Runtime libraries and link integration are Phase 24; GC/EH/interop helpers are Phase 25; RID/publish packaging is Phase 26.

## 8. Maintained fork versus external adapter

Maintained patch (two internal files): `RyuJitCompilationBuilder.cs` parses the closed seam options; `RyuJitCompilation.cs` replaces RyuJIT codegen and ObjectWriter only when those options are complete and valid. External code: `HybridCPU.Compiler.NativeAot.Adapter` owns the versioned request/result, restricted CIL import, and Core compilation. Core remains unchanged and frontend-neutral.

Target enum/parser, real HybridCPU node factory, object writer, runtime metadata, runtime packs, and SDK targets are not claimed by this phase. They require later maintained-fork changes unless upstream exposes a suitable seam.

## 9. Stability classification

- Public/stable for our codebase: `NativeAotSeamRequestV1`, `NativeAotSeamArtifactV1`, Core/CIL contracts.
- Internal and intentionally patched: `RyuJitCompilationBuilder.UseBackendOptions`, `RyuJitCompilation.CompileSingleMethod`, `CompileInternal`, `MethodCodeNode.SetCode`, and dependency-graph node types.
- Read-only upstream implementation dependencies: target/type-system, rooting, metadata, object writer, and BuildIntegration targets.
- Unsuitable dependencies: RyuJIT `CorInfoImpl`, host JIT code bytes/registers/calling convention, elapsed-time decisions, reflection loading of ILCompiler implementation assemblies into Core, and a presumed generic backend plug-in.

## 10. Deterministic version-skew detection

The adapter requires the exact source commit and patch SHA-256. The patch validates the commit literal and a 64-hex patch digest before graph execution; the adapter validates both against compiled constants. Tests hash the checked-in patch and assert the fourteen clean-source file hashes below. `Verify-Phase23NativeAotSeam.ps1` additionally requires either an exactly clean baseline to which the patch applies or the exact already-patched reverse state. Any mismatch fails with `HCNAOT1001/1002` or a patch-drift error; accidental API compatibility is not accepted.

## Clean-source hash manifest

```text
6787e1f59f5e27d773f5f1564e00deded72ead529a80be3a05bbb22334474904  src/coreclr/tools/Common/TypeSystem/Common/TargetArchitecture.cs
aa81c37ea6e0d288ec58a3582a214bb51d3717183d94abcaa3b1811892ce2f66  src/coreclr/tools/Common/TypeSystem/Common/TargetDetails.cs
1e76a64cf06b20ed12917d4e63009860bf4948f26a176b8a8449a7065683d7a4  src/coreclr/tools/Common/CommandLineHelpers.cs
86cd3e57e9ddb12c6db11efd3c61c77c6b28f2fe2be01040d5db4a8ad1f08d1c  src/coreclr/tools/aot/ILCompiler/ILCompilerRootCommand.cs
fdd1e2c6cb47d67b7f920a449d155434f8ad07a5779a574e9fcb1f52a5db37b6  src/coreclr/tools/aot/ILCompiler/Program.cs
47b031948f7c8280ad91284fa539cb90e2a02264c9431408eb29157650493c0d  src/coreclr/tools/aot/ILCompiler.RyuJit/Compiler/RyuJitCompilationBuilder.cs
65b6524eb722020dbd155342adab989133db1b63d6eb44cb720672d182beebb7  src/coreclr/tools/aot/ILCompiler.RyuJit/Compiler/RyuJitCompilation.cs
60ff0edfb63bf5c350c78aeda7df1264938c4df0fd0b6f8d6622dbf1b6b5febe  src/coreclr/tools/aot/ILCompiler.RyuJit/Compiler/DependencyAnalysis/MethodCodeNode.cs
202c109e628ce502a2f3212af8fa4c1e379bc9e42ce8f5947365a477a937131a  src/coreclr/tools/aot/ILCompiler.Compiler/Compiler/Compilation.cs
264d411fffed2c5aa9742f3fea7dd6b905c225baa2781df20f584b28f8e2b0c7  src/coreclr/tools/aot/ILCompiler.Compiler/Compiler/DependencyAnalysis/NodeFactory.cs
fd8c0f1ab1e6f2166253143e8f9e96481cc52a8cf929fab1b3e651d3fc771a25  src/coreclr/tools/Common/Compiler/DependencyAnalysis/ObjectDataBuilder.cs
a0758d2aa028835da4017a46320b9f3d269ba7b9e6b14bb1fc8b838d0055bdbf  src/coreclr/tools/Common/Compiler/DependencyAnalysis/Relocation.cs
d4d71b4676b5d632ea7c7a31433e654a1e998e14a073f4ce9bac444bec1a4cd2  src/coreclr/tools/Common/Compiler/DependencyAnalysis/ObjectNodeSection.cs
fabeb9083c4c20e254d29eb833a5a751ba55b2d1bef97bd05f7f55c4ee8de18f  src/coreclr/tools/aot/ILCompiler.Compiler/Compiler/ObjectWriter/ObjectWriter.cs
```

## Authority and release disposition

The seam emits compiler artifacts and diagnostics only. It has no runtime-legality, execution, publication, commit, or retire authority. It is opt-in, single-method, relocation-free, and not release-authorized. Its deterministic fallback is failure; it never selects RyuJIT or host code as a fallback.
