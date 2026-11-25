# 94 — Индекс кода

Пути проверены 9 ноября 2026. Индекс показывает owners; наличие файла не является acceptance. Actual Compile/ProjectReference/TargetPath проверяются с параметрами конкретной сборки.

| Ответственность | Код |
|---|---|
| Canonical facade | [HybridCpuCanonicalCompiler](../../../Compilers/HybridCPU_Compiler/Core/Compilation/HybridCpuCanonicalCompiler.cs) |
| IR compilation | [HybridCpuIrProgramCompiler](../../../Compilers/HybridCPU_Compiler/Core/IR/Compilation/HybridCpuIrProgramCompiler.cs) |
| Shared bundling / allocation ordering | [HybridCpuProgramBundler](../../../Compilers/HybridCPU_Compiler/Core/IR/Compilation/HybridCpuProgramBundler.cs) |
| Lowering / relocation / serialization | [HybridCpuPostBundleCompiler](../../../Compilers/HybridCPU_Compiler/Core/IR/Compilation/HybridCpuPostBundleCompiler.cs) |
| Runtime transport / emission | [adapter](../../../Compilers/HybridCPU_Compiler/API/Runtime/NativeTransportRuntimeAdapter.cs), [emission](../../../Compilers/HybridCPU_Compiler/API/Runtime/NativeTransportRuntimeAdapter.Emission.cs) |
| Source compilation / admission | [ManagedSourceCompanionV1](../../../Compilers/HybridCPU_Compiler/Roslyn/ManagedSourceCompanionV1.cs), [NoHeap](../../../Compilers/HybridCPU_Compiler/Analyzers/KernelNoHeapAnalyzer.cs) |
| Reference roles / BodyWorld | [input](../../../Compilers/HybridCPU_Compiler/Roslyn/ManagedSourceCompanionV1.Input.cs), [BodyWorld](../../../Compilers/HybridCPU_Compiler/Roslyn/ManagedSourceCompanionV1.BodyWorld.cs) |
| Source CLI | [ManagedSourceCommandLineV1](../../../Compilers/HybridCPU_Compiler/Roslyn/ManagedSourceCommandLineV1.cs), [usage](../../../Compilers/HybridCPU_Compiler/Roslyn/README.md) |
| CIL decode / envelope | [RestrictedCilImporterV1](../../../Compilers/HybridCPU_Compiler/Cil/RestrictedCilImporterV1.cs) |
| Metadata / exact generic signatures | [MetadataBindingsV1](../../../Compilers/HybridCPU_Compiler/Cil/RestrictedCilImporterV1.MetadataBindingsV1.cs) |
| Graph / module lifetime | [CallGraphV1](../../../Compilers/HybridCPU_Compiler/Cil/RestrictedCilImporterV1.CallGraphV1.cs), [ModuleContext](../../../Compilers/HybridCPU_Compiler/Cil/RestrictedCilImporterV1.ModuleContext.cs) |
| CFG / SSA | [ControlFlowV2](../../../Compilers/HybridCPU_Compiler/Cil/RestrictedCilImporterV1.ControlFlowV2.cs) |
| Nullable / generic local zero | [NullableInt32](../../../Compilers/HybridCPU_Compiler/Cil/RestrictedCilImporterV1.NullableInt32.cs) |
| Scalar projections / purity | [StaticProjectionV1](../../../Compilers/HybridCPU_Compiler/Cil/RestrictedCilImporterV1.StaticProjectionV1.cs), [ExternalScalarValues](../../../Compilers/HybridCPU_Compiler/Cil/RestrictedCilImporterV1.ExternalScalarValues.cs) |
| Maps / object link | [ManagedSourceMapV1](../../../Compilers/HybridCPU_Compiler/Cil/ManagedSourceMapV1.cs), [ScalarControlFlowV2ObjectLinkerV1](../../../Compilers/HybridCPU_Compiler/Cil/ScalarControlFlowV2ObjectLinkerV1.cs) |
| Physical native ABI | [HybridCpuNativeAbiContractV2](../../../Compilers/HybridCPU_Compiler/Core/Target/HybridCpuNativeAbiContractV2.cs) |
| Source regressions | [CompilerProfileSmoke](../../../Compilers/HybridCPU_Compiler/Tools/CompilerProfileSmoke/Program.cs), [nullable](../../../Compilers/HybridCPU_Compiler/Tools/CompilerProfileSmoke/NullableInt32Checks.cs), [I8 values](../../../Compilers/HybridCPU_Compiler/Tools/CompilerProfileSmoke/Int64ValueChecks.cs) |
| Physical / serialized oracle | [ManagedPortSmoke](../../../Compilers/HybridCPU_Compiler/Tools/ManagedPortSmoke/Program.cs), [nullable](../../../Compilers/HybridCPU_Compiler/Tools/ManagedPortSmoke/NullableValueSmoke.cs), [evaluator](../../../Compilers/HybridCPU_Compiler/Tools/ManagedPortSmoke/NativeConstantSmoke.cs), [wide-rem](../../../Compilers/HybridCPU_Compiler/Tools/ManagedPortSmoke/UnsignedDivisionSmoke.cs) |
| Scheduler measurement | [SchedulerBenchmark](../../../Compilers/HybridCPU_Compiler/Tools/SchedulerBenchmark/SchedulerBenchmark.csproj) |
| Release evidence boundaries | [ReleaseQualification README](../../../Compilers/HybridCPU_Compiler/ReleaseQualification/README.md) |
| Toolchain / isolation | [global.json](../../../global.json), [Isolation.props](../../../Compilers/CpuInterfaceBridge/Validation/Isolation.props), [SDK Roslyn references](../../../Compilers/HybridCPU_Compiler/Roslyn/SdkRoslynHost.targets) |

Project boundaries: [Core](../../../Compilers/HybridCPU_Compiler/Core/HybridCPU.Compiler.Core.csproj), [CIL](../../../Compilers/HybridCPU_Compiler/Cil/HybridCPU.Compiler.Cil.csproj), [Roslyn](../../../Compilers/HybridCPU_Compiler/Roslyn/HybridCPU.Compiler.Roslyn.csproj), [upper compiler](../../../Compilers/HybridCPU_Compiler/HybridCPU_Compiler.csproj), [NativeAOT adapter](../../../Compilers/HybridCPU_Compiler/NativeAot/HybridCPU.Compiler.NativeAot.Adapter.csproj), [LLVM](../../../Compilers/HybridCPU_Compiler/LLVM/HybridCPU.Compiler.Llvm.csproj).
