using HybridCPU.Compiler.Cil;
using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.Target.Managed;
using HybridCPU.Compiler.Core.Target.Object;

internal static class StaticProjectionSmoke
{
    public static void Run()
    {
        byte[] pe = File.ReadAllBytes(typeof(ProjectionFixture).Assembly.Location);
        RestrictedCilImporterV1 importer = new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2);
        ManagedCallGraphCompilationV1 graph = importer.ImportBodyWorld(new(ManagedBodyWorldModeV1.StandaloneRestrictedModule, pe, "projection-smoke",
            [new(typeof(ProjectionFixture).FullName!, nameof(ProjectionFixture.Read))], []));
        if (graph.Status != RestrictedCilImportStatusV1.Success)
            throw new Exception(string.Join(";", graph.Diagnostics.Select(d => d.Code + ":" + d.Message)));
        if (!graph.Graph!.Edges.Any(e => e.IsTypeInitializerTarget && e.CalleeIdentity.Contains("ProjectionAngle..cctor", StringComparison.Ordinal)) ||
            graph.Methods.Any(m => m.Identity.MethodName == "get_Value"))
            throw new Exception("Folded getter must keep its owner cctor reachable, without a byref getter body");
        ScalarControlFlowV2LinkedProgramV1 linkedGraph = new ScalarControlFlowV2ObjectLinkerV1().Link(graph, graph.Graph.RootIdentities.Single());
        ManagedCompiledMethodV1 cctorMethod = graph.Methods.Single(m => m.Identity.MethodName == ".cctor");
        ManagedTypeUniverseRowV1 cctorType = graph.TypeUniverse!.Rows.Single(row =>
            row.StableIdentity == cctorMethod.Identity.DeclaringType);
        if (linkedGraph.Status != ScalarControlFlowV2LinkStatusV1.Success ||
            linkedGraph.RestrictedImage?.RuntimeBootstrap?.ModuleInitializers is not [{ } initializer] ||
            initializer.InitializerSymbol != cctorMethod.Identity.StableIdentity ||
            initializer.TypeId != cctorType.TypeId || initializer.Order != 0)
            throw new Exception("Linked cctor must have one exact TypeId-bound bootstrap registration: " +
                string.Join(';', linkedGraph.Diagnostics.Select(d => d.Code + ":" + d.Message)));
        ManagedWorkstreamDiscoveryV1 discovery = ManagedWorkstreamDiscoveryContractV1.Discover(graph, linkedGraph);
        if (!discovery.IsComplete ||
            !discovery.RequiredWorkstreams.Contains("static-type-initialization", StringComparer.Ordinal) ||
            !discovery.RequiredWorkstreams.Contains("type-layout-object-references", StringComparer.Ordinal) ||
            !discovery.RequiredWorkstreams.Contains("gc-maps-safepoints", StringComparer.Ordinal) ||
            discovery.RequiredWorkstreams.Contains("eh-unwind", StringComparer.Ordinal) ||
            linkedGraph.LinkedRuntimeModuleIdentities?.Contains(
                HybridCPU.Compiler.Core.Target.Managed.HybridCpuManagedEnsureTypeInitializedEmitterV1.ModuleIdentity,
                StringComparer.Ordinal) != true)
            throw new Exception("Post-link workstream discovery must derive the exact static/type/GC closure without granting EH: " +
                string.Join(',', discovery.RequiredWorkstreams) + "; unknown=" +
                string.Join(',', discovery.UnclassifiedRuntimeModules));
        RestrictedCilImportResultV1 read = graph.Methods.Single(m => m.Identity.MethodName == nameof(ProjectionFixture.Read)).Import;
        string?[] helpers = read.Program!.Instructions.Select(i => i.Annotation.BranchTargetSymbolName).ToArray();
        if (!helpers.Contains("__hybridcpu_managed_ensure_type_initialized") || !helpers.Contains("__hybridcpu_managed_static_load_i4"))
            throw new Exception("Projected static read must retain type-init and runtime static read helpers");
        string?[] directHelpers = graph.Methods.Single(m => m.Identity.DeclaringType == typeof(ProjectionAngle).FullName &&
                m.Identity.MethodName == ".cctor").Import
            .Program!.Instructions.Select(i => i.Annotation.BranchTargetSymbolName).ToArray();
        if (!directHelpers.Contains("__hybridcpu_managed_ensure_type_initialized") ||
            !directHelpers.Contains("__hybridcpu_managed_static_store_i4"))
            throw new Exception("Direct projected static value stores in the cctor must retain type-init/static helper effects");
        HybridCpuRuntimeHelperV1? ensureAbi = HybridCPU.Compiler.Core.Target.Managed.HybridCpuManagedAbiFamilyV1.Default
            .ResolveRuntimeHelper(HybridCPU.Compiler.Core.Target.Managed.HybridCpuManagedEnsureTypeInitializedEmitterV1.Symbol);
        HybridCpuObjectArtifactV1 ensureObject = HybridCPU.Compiler.Core.Target.Managed.HybridCpuManagedEnsureTypeInitializedEmitterV1.EmitObject();
        if (ensureAbi is not {
                GcTransition: HybridCPU.Compiler.Core.Target.Managed.HybridCpuRuntimeHelperGcTransitionV1.MaySafepoint,
                Support: HybridCPU.Compiler.Core.Target.Managed.HybridCpuManagedAbiSupportV1.Supported } ||
            ensureObject.Status != HybridCPU.Compiler.Core.Target.Object.HybridCpuObjectStatusV1.Success ||
            !ensureObject.Symbols.Any(symbol => symbol.Name ==
                HybridCPU.Compiler.Core.Target.Managed.HybridCpuManagedEnsureTypeInitializedEmitterV1.Symbol && symbol.IsDefinition))
            throw new Exception("Type initialization must bind a supported MaySafepoint CPU-callable helper thunk");
        HybridCpuObjectArtifactV1 storeObject = HybridCPU.Compiler.Core.Target.Managed.HybridCpuManagedStaticStoreInt32EmitterV1.EmitObject();
        if (storeObject.Status != HybridCPU.Compiler.Core.Target.Object.HybridCpuObjectStatusV1.Success ||
            !storeObject.Symbols.Any(symbol => symbol.Name ==
                HybridCPU.Compiler.Core.Target.Managed.HybridCpuManagedStaticStoreInt32EmitterV1.Symbol && symbol.IsDefinition) ||
            HybridCPU.Platform.Contracts.HybridCpuExternalServiceEcallContractV1.MaximumRegisterArguments < 3)
            throw new Exception("Static i4 store must bind a three-scalar-argument CPU-callable helper thunk");
        HybridCpuObjectArtifactV1 storeReferenceObject = HybridCPU.Compiler.Core.Target.Managed.HybridCpuManagedStaticStoreReferenceEmitterV1.EmitObject();
        HybridCpuRuntimeHelperV1? storeReferenceAbi = HybridCPU.Compiler.Core.Target.Managed.HybridCpuManagedAbiFamilyV1.Default
            .ResolveRuntimeHelper(HybridCPU.Compiler.Core.Target.Managed.HybridCpuManagedStaticStoreReferenceEmitterV1.Symbol);
        if (storeReferenceAbi is not {
                GcTransition: HybridCPU.Compiler.Core.Target.Managed.HybridCpuRuntimeHelperGcTransitionV1.None,
                MayThrow: false, Support: HybridCPU.Compiler.Core.Target.Managed.HybridCpuManagedAbiSupportV1.Supported } ||
            storeReferenceObject.Status != HybridCPU.Compiler.Core.Target.Object.HybridCpuObjectStatusV1.Success ||
            !storeReferenceObject.Symbols.Any(symbol => symbol.Name ==
                HybridCPU.Compiler.Core.Target.Managed.HybridCpuManagedStaticStoreReferenceEmitterV1.Symbol && symbol.IsDefinition))
            throw new Exception("Static reference store must bind an exact non-allocating CPU-callable helper thunk");
        HybridCpuObjectArtifactV1 loadObject = HybridCPU.Compiler.Core.Target.Managed.HybridCpuManagedStaticLoadInt32EmitterV1.EmitObject();
        if (loadObject.Status != HybridCPU.Compiler.Core.Target.Object.HybridCpuObjectStatusV1.Success ||
            !loadObject.Symbols.Any(symbol => symbol.Name ==
                HybridCPU.Compiler.Core.Target.Managed.HybridCpuManagedStaticLoadInt32EmitterV1.Symbol && symbol.IsDefinition))
            throw new Exception("Static i4 load must bind a two-scalar-argument CPU-callable helper thunk");
        HybridCpuObjectArtifactV1 loadReferenceObject = HybridCPU.Compiler.Core.Target.Managed.HybridCpuManagedStaticLoadReferenceEmitterV1.EmitObject();
        HybridCpuRuntimeHelperV1? loadReferenceAbi = HybridCPU.Compiler.Core.Target.Managed.HybridCpuManagedAbiFamilyV1.Default
            .ResolveRuntimeHelper(HybridCPU.Compiler.Core.Target.Managed.HybridCpuManagedStaticLoadReferenceEmitterV1.Symbol);
        if (loadReferenceAbi is not {
                GcTransition: HybridCPU.Compiler.Core.Target.Managed.HybridCpuRuntimeHelperGcTransitionV1.None,
                MayThrow: false, Support: HybridCPU.Compiler.Core.Target.Managed.HybridCpuManagedAbiSupportV1.Supported } ||
            loadReferenceObject.Status != HybridCPU.Compiler.Core.Target.Object.HybridCpuObjectStatusV1.Success ||
            !loadReferenceObject.Symbols.Any(symbol => symbol.Name ==
                HybridCPU.Compiler.Core.Target.Managed.HybridCpuManagedStaticLoadReferenceEmitterV1.Symbol && symbol.IsDefinition))
            throw new Exception("Static reference load must bind an exact non-allocating CPU-callable helper thunk");
        HybridCpuRuntimeHelperV1? nullAbi = HybridCPU.Compiler.Core.Target.Managed.HybridCpuManagedAbiFamilyV1.Default
            .ResolveRuntimeHelper(HybridCPU.Compiler.Core.Target.Managed.HybridCpuManagedNullCheckEmitterV1.Symbol);
        HybridCpuObjectArtifactV1 nullObject = HybridCPU.Compiler.Core.Target.Managed.HybridCpuManagedNullCheckEmitterV1.EmitObject();
        if (nullAbi is not {
                GcTransition: HybridCPU.Compiler.Core.Target.Managed.HybridCpuRuntimeHelperGcTransitionV1.RequiredSafepoint,
                MayThrow: true,
                Support: HybridCPU.Compiler.Core.Target.Managed.HybridCpuManagedAbiSupportV1.Supported } ||
            nullObject.Status != HybridCPU.Compiler.Core.Target.Object.HybridCpuObjectStatusV1.Success ||
            !nullObject.Symbols.Any(symbol => symbol.Name ==
                HybridCPU.Compiler.Core.Target.Managed.HybridCpuManagedNullCheckEmitterV1.Symbol && symbol.IsDefinition))
            throw new Exception("Explicit null check must be an allocating, throwing CPU-callable helper");
        ManagedCallGraphCompilationV1 scalarArgument = importer.ImportBodyWorld(new(ManagedBodyWorldModeV1.StandaloneRestrictedModule, pe,
            "projection-scalar-argument", [new(typeof(ProjectionFixture).FullName!, nameof(ProjectionFixture.ReadDirect))], []));
        if (scalarArgument.Status != RestrictedCilImportStatusV1.Success)
            throw new Exception("Scalar value argument projection: " + string.Join(";", scalarArgument.Diagnostics.Select(d => d.Code + ":" + d.Message)));
        IrProgram raw = scalarArgument.Methods.Single(m => m.Identity.MethodName == nameof(ProjectionAngle.Raw)).Import.Program!;
        if (raw.Instructions.Count == 0 ||
            raw.Instructions.Any(i => i.Annotation.BranchTargetSymbolName == "__hybridcpu_managed_null_check"))
            throw new Exception("Exact scalar value field read must be an SSA copy without object/null/byref memory semantics");
        ManagedCallGraphCompilationV1 scalarLocal = importer.ImportBodyWorld(new(ManagedBodyWorldModeV1.StandaloneRestrictedModule, pe,
            "projection-scalar-local", [new(typeof(ProjectionFixture).FullName!, nameof(ProjectionFixture.SelectLocal))], []));
        if (scalarLocal.Status != RestrictedCilImportStatusV1.Success)
            throw new Exception("Exact scalar value local projection: " +
                string.Join(";", scalarLocal.Diagnostics.Select(d => d.Code + ":" + d.Message)));
        ManagedCallGraphCompilationV1 scalarArgumentPhi = importer.ImportBodyWorld(new(ManagedBodyWorldModeV1.StandaloneRestrictedModule, pe,
            "projection-scalar-argument-phi", [new(typeof(ProjectionFixture).FullName!, nameof(ProjectionFixture.AdjustArgument))], []));
        if (scalarArgumentPhi.Status != RestrictedCilImportStatusV1.Success)
            throw new Exception("Projected mutable argument CFG join: " +
                string.Join(";", scalarArgumentPhi.Diagnostics.Select(d => d.Code + ":" + d.Message)));
        ManagedCallGraphCompilationV1 unsigned = importer.ImportBodyWorld(new(ManagedBodyWorldModeV1.StandaloneRestrictedModule, pe, "projection-unsigned",
            [new(typeof(ProjectionFixture).FullName!, nameof(ProjectionFixture.ReadUnsigned))], []));
        if (unsigned.Status != RestrictedCilImportStatusV1.Success) throw new Exception("I4 stack bits must return as UInt32 without reinterpretation");
        ManagedCallGraphCompilationV1 unsignedConstant = importer.ImportBodyWorld(new(ManagedBodyWorldModeV1.StandaloneRestrictedModule, pe,
            "projection-unsigned-constant", [new(typeof(ProjectionFixture).FullName!, nameof(ProjectionFixture.DivideByEight))], []));
        if (unsignedConstant.Status != RestrictedCilImportStatusV1.Success)
            throw new Exception("Canonical ldc.i4 must satisfy an exact UInt32 managed-call parameter: " +
                string.Join(";", unsignedConstant.Diagnostics.Select(d => d.Code + ":" + d.Message)));
        ManagedCallGraphCompilationV1 instanceStore = importer.ImportBodyWorld(new(ManagedBodyWorldModeV1.StandaloneRestrictedModule, pe,
            "projection-instance-store", [new(typeof(ProjectionFixture).FullName!, nameof(ProjectionFixture.CreateHolder))], []));
        if (instanceStore.Status != RestrictedCilImportStatusV1.Success)
            throw new Exception("Exact scalar value instance field store: " +
                string.Join(";", instanceStore.Diagnostics.Select(d => d.Code + ":" + d.Message)));
        IrProgram holderCtor = instanceStore.Methods.Single(m => m.Identity.DeclaringType == typeof(ProjectionHolder).FullName &&
                m.Identity.MethodName == ".ctor").Import.Program!;
        if (!holderCtor.Instructions.Any(i => i.Annotation.BranchTargetSymbolName == "__hybridcpu_managed_null_check") ||
            !holderCtor.Instructions.Any(i => i.Opcode == HybridCPU.Compiler.Core.IR.HybridCpuOpcode.SW))
            throw new Exception("Projected instance value store must retain object null-check and physical i4 write");
        ScalarControlFlowV2LinkedProgramV1 nullGate = new ScalarControlFlowV2ObjectLinkerV1().Link(
            instanceStore, instanceStore.Graph!.RootIdentities.Single());
        if (!nullGate.Diagnostics.Any(diagnostic => diagnostic.Code == "HCSCF-LINK4024" &&
                diagnostic.Message.Contains("OutOfMemoryException", StringComparison.Ordinal) &&
                diagnostic.Message.Contains("native", StringComparison.OrdinalIgnoreCase)))
            throw new Exception("Native object allocation must fail closed before the dependent null-check without OOM/EH evidence");
        ManagedCallGraphCompilationV1 instanceRead = importer.ImportBodyWorld(new(ManagedBodyWorldModeV1.StandaloneRestrictedModule, pe,
            "projection-instance-read", [new(typeof(ProjectionFixture).FullName!, nameof(ProjectionFixture.ReadHolder))], []));
        if (instanceRead.Status != RestrictedCilImportStatusV1.Success)
            throw new Exception("Exact scalar instance field load: " + string.Join(';', instanceRead.Diagnostics));
        IrProgram load = instanceRead.Methods.Single(m => m.Identity.MethodName == nameof(ProjectionFixture.ReadHolder)).Import.Program!;
        if (!load.Instructions.Any(i => i.Annotation.BranchTargetSymbolName == "__hybridcpu_managed_null_check") ||
            !load.Instructions.Any(i => i.Opcode == HybridCPU.Compiler.Core.IR.HybridCpuOpcode.LWU))
            throw new Exception("Object-contained scalar payload requires null check and physical load, never receiver SSA-copy");
        ManagedCallGraphCompilationV1 rejected = importer.ImportBodyWorld(new(ManagedBodyWorldModeV1.StandaloneRestrictedModule, pe,
            "projection-instance-gc-negative", [new(typeof(ProjectionFixture).FullName!, nameof(ProjectionFixture.CopyReferencePayload))], []));
        if (!rejected.Diagnostics.Any(d => d.Code == "HCCIL1209"))
            throw new Exception("Reference-containing instance value copy must not acquire scalar projection: " + string.Join(';', rejected.Diagnostics));
        ManagedCallGraphCompilationV1 narrow = importer.ImportBodyWorld(new(ManagedBodyWorldModeV1.StandaloneRestrictedModule, pe,
            "byte-field-store", [new(typeof(ProjectionFixture).FullName!, nameof(ProjectionFixture.StoreByte))], []));
        if (narrow.Status != RestrictedCilImportStatusV1.Success ||
            !narrow.Methods.Single().Import.Program!.Instructions.Any(i => i.Opcode == HybridCPU.Compiler.Core.IR.HybridCpuOpcode.SB) ||
            !narrow.Methods.Single().Import.Program!.Instructions.Any(i => i.Annotation.BranchTargetSymbolName == "__hybridcpu_managed_null_check"))
            throw new Exception("Byte store must emit exact SB with null-check: " + string.Join(';', narrow.Diagnostics));
        ManagedCallGraphCompilationV1 boolStore = importer.ImportBodyWorld(new(ManagedBodyWorldModeV1.StandaloneRestrictedModule, pe,
            "bool-field-store", [new(typeof(ProjectionFixture).FullName!, nameof(ProjectionFixture.StoreBool))], []));
        if (boolStore.Status != RestrictedCilImportStatusV1.Success ||
            !boolStore.Methods.Single().Import.Program!.Instructions.Any(i => i.Opcode == HybridCPU.Compiler.Core.IR.HybridCpuOpcode.SB))
            throw new Exception("Bool field store must preserve its one-byte physical storage");
        ManagedCallGraphCompilationV1 shortStore = importer.ImportBodyWorld(new(ManagedBodyWorldModeV1.StandaloneRestrictedModule, pe,
            "short-field-store", [new(typeof(ProjectionFixture).FullName!, nameof(ProjectionFixture.StoreShort))], []));
        if (shortStore.Status != RestrictedCilImportStatusV1.Success ||
            !shortStore.Methods.Single().Import.Program!.Instructions.Any(i => i.Opcode == HybridCPU.Compiler.Core.IR.HybridCpuOpcode.SH) ||
            !shortStore.Methods.Single().Import.Program!.Instructions.Any(i => i.Annotation.BranchTargetSymbolName == "__hybridcpu_managed_null_check"))
            throw new Exception("Two-byte store must emit exact SH with null-check: " + string.Join(';', shortStore.Diagnostics));
        foreach (string method in new[] { nameof(ProjectionFixture.Mutable), nameof(ProjectionFixture.Computed), nameof(ProjectionFixture.SideEffect) })
        {
            RestrictedCilImportResultV1 result = importer.ImportImage(pe, new(typeof(ProjectionFixture).FullName!, method));
            if (!result.Diagnostics.Any(d => d.Code == "HCCIL1820"))
                throw new Exception(method + " must not fold: " + string.Join(";", result.Diagnostics.Select(d => d.Code + ":" + d.Message)));
        }
        if (ProjectionFixture.Read() != 0 || ProjectionAngle.InitCount != 1 || ProjectionFixture.Read() != 0 || ProjectionAngle.InitCount != 1)
            throw new Exception("CoreCLR static getter/cctor reference behavior");
        Console.WriteLine("PASS exact static scalar value/getter projections, retained cctor/helper effects, aggregate and getter negatives");
    }
}

public readonly struct ProjectionAngle
{
    private readonly uint _value;
    public ProjectionAngle(uint value) { _value = value; }
    public static readonly ProjectionAngle Angle90;
    public static ProjectionAngle Mutable;
    public static int InitCount;
    static ProjectionAngle() { InitCount = 1; }
    public uint Value => _value;
    public uint Computed => _value + 1;
    public uint Effect { get { InitCount++; return _value; } }
    public static uint Raw(ProjectionAngle value) => value._value;
    public static ProjectionAngle Divide(ProjectionAngle value, uint divisor) => new(value._value / divisor);
}
public static class ProjectionFixture
{
    public static int Read() => unchecked((int)ProjectionAngle.Angle90.Value);
    public static uint ReadUnsigned() => ProjectionAngle.Angle90.Value;
    public static uint Mutable() => ProjectionAngle.Mutable.Value;
    public static uint Computed() => ProjectionAngle.Angle90.Computed;
    public static uint SideEffect() => ProjectionAngle.Angle90.Effect;
    public static uint ReadDirect() => ProjectionAngle.Raw(ProjectionAngle.Angle90);
    public static ProjectionAngle SelectLocal(bool first)
    {
        ProjectionAngle value;
        if (first) value = ProjectionAngle.Angle90;
        else value = ProjectionAngle.Divide(ProjectionAngle.Angle90, 8u);
        return value;
    }
    public static ProjectionAngle AdjustArgument(ProjectionAngle value, bool adjust)
    {
        if (adjust) value = ProjectionAngle.Divide(value, 8u);
        return value;
    }
    public static ProjectionAngle DivideByEight() => ProjectionAngle.Divide(ProjectionAngle.Angle90, 8u);
    public static ProjectionHolder CreateHolder() => new(ProjectionAngle.Angle90);
    public static ProjectionAngle ReadHolder(ProjectionHolder holder) => holder.Value;
    public static void StoreByte(ProjectionByteHolder holder, byte value) => holder.Value = value;
    public static void StoreBool(ProjectionByteHolder holder, bool value) => holder.Flag = value;
    public static void StoreShort(ProjectionByteHolder holder, short value) => holder.Short = value;
    public static void CopyReferencePayload(ProjectionReferenceHolder to, ProjectionReferenceHolder from) => to.Value = from.Value;
}
public struct ProjectionReferencePayload { public object Reference; public int Number; }
public sealed class ProjectionByteHolder { public byte Value; public bool Flag; public short Short; }
public sealed class ProjectionReferenceHolder { public ProjectionReferencePayload Value; }
public sealed class ProjectionHolder
{
    public ProjectionAngle Value;
    public ProjectionHolder(ProjectionAngle value) { Value = value; }
}
