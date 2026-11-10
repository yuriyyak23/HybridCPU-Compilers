using System.Reflection;
using System.Reflection.Emit;
using HybridCPU.Compiler.Cil;
using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.Target.Managed;
using HybridCPU.Compiler.Core.Target.Object;

internal static class ArgumentStoreSmoke
{
    public static void Run()
    {
        PersistedAssemblyBuilder assembly = new PersistedAssemblyBuilder(new AssemblyName("RawArgumentStoreFixture"), typeof(object).Assembly);
        TypeBuilder type = assembly.DefineDynamicModule("ArgumentStore").DefineType("ArgumentStore", TypeAttributes.Public);

        ILGenerator choose = type.DefineMethod("Choose", MethodAttributes.Public | MethodAttributes.Static,
            typeof(int), [typeof(int), typeof(bool)]).GetILGenerator();
        Label join = choose.DefineLabel();
        choose.Emit(OpCodes.Ldarg_1); choose.Emit(OpCodes.Brfalse_S, join);
        choose.Emit(OpCodes.Ldc_I4_7); choose.Emit(OpCodes.Starg_S, (short)0);
        choose.MarkLabel(join); choose.Emit(OpCodes.Ldarg_0); choose.Emit(OpCodes.Ret);

        ILGenerator wrong = type.DefineMethod("WrongType", MethodAttributes.Public | MethodAttributes.Static,
            typeof(object), [typeof(object)]).GetILGenerator();
        wrong.Emit(OpCodes.Ldc_I4_1); wrong.Emit(OpCodes.Starg_S, (short)0);
        wrong.Emit(OpCodes.Ldarg_0); wrong.Emit(OpCodes.Ret);

        ILGenerator absent = type.DefineMethod("Absent", MethodAttributes.Public | MethodAttributes.Static,
            typeof(void), []).GetILGenerator();
        absent.Emit(OpCodes.Ldc_I4_1); absent.Emit(OpCodes.Starg_S, (short)0); absent.Emit(OpCodes.Ret);

        ILGenerator decrement = type.DefineMethod("PostDecrement", MethodAttributes.Public | MethodAttributes.Static,
            typeof(int), [typeof(int)]).GetILGenerator();
        decrement.DeclareLocal(typeof(int));
        Label decrementLoop = decrement.DefineLabel();
        decrement.Emit(OpCodes.Ldarg_0); decrement.Emit(OpCodes.Stloc_0);
        decrement.MarkLabel(decrementLoop);
        decrement.Emit(OpCodes.Ldloc_0); decrement.Emit(OpCodes.Dup); decrement.Emit(OpCodes.Ldc_I4_1);
        decrement.Emit(OpCodes.Sub); decrement.Emit(OpCodes.Stloc_0); decrement.Emit(OpCodes.Brtrue_S, decrementLoop);
        decrement.Emit(OpCodes.Ldloc_0); decrement.Emit(OpCodes.Ret);

        ILGenerator twoLatch = type.DefineMethod("TwoLatchLoop", MethodAttributes.Public | MethodAttributes.Static,
            typeof(int), [typeof(int)]).GetILGenerator();
        twoLatch.DeclareLocal(typeof(int));
        Label header = twoLatch.DefineLabel(), firstLatch = twoLatch.DefineLabel(), secondLatch = twoLatch.DefineLabel();
        twoLatch.Emit(OpCodes.Ldc_I4_0); twoLatch.Emit(OpCodes.Stloc_0); twoLatch.Emit(OpCodes.Br_S, header);
        twoLatch.MarkLabel(firstLatch); twoLatch.Emit(OpCodes.Ldc_I4_1); twoLatch.Emit(OpCodes.Stloc_0); twoLatch.Emit(OpCodes.Br_S, header);
        twoLatch.MarkLabel(secondLatch); twoLatch.Emit(OpCodes.Ldc_I4_2); twoLatch.Emit(OpCodes.Stloc_0); twoLatch.Emit(OpCodes.Br_S, header);
        twoLatch.MarkLabel(header); twoLatch.Emit(OpCodes.Ldarg_0);
        twoLatch.Emit(OpCodes.Switch, new[] { firstLatch, secondLatch });
        twoLatch.Emit(OpCodes.Ldloc_0); twoLatch.Emit(OpCodes.Ret);

        MethodBuilder touchMethod = type.DefineMethod("Touch", MethodAttributes.Public | MethodAttributes.Static,
            typeof(void), [typeof(int)]);
        ILGenerator touch = touchMethod.GetILGenerator();
        touch.Emit(OpCodes.Ret);
        ILGenerator ingressLoop = type.DefineMethod("IngressLoop", MethodAttributes.Public | MethodAttributes.Static,
            typeof(int), [typeof(int), typeof(int)]).GetILGenerator();
        Label ingressHeader = ingressLoop.DefineLabel();
        ingressLoop.MarkLabel(ingressHeader);
        ingressLoop.Emit(OpCodes.Ldarg_1); ingressLoop.Emit(OpCodes.Call, touchMethod);
        ingressLoop.Emit(OpCodes.Ldarg_0); ingressLoop.Emit(OpCodes.Ldc_I4_1); ingressLoop.Emit(OpCodes.Sub);
        ingressLoop.Emit(OpCodes.Dup); ingressLoop.Emit(OpCodes.Starg_S, (short)0);
        ingressLoop.Emit(OpCodes.Brtrue_S, ingressHeader);
        ingressLoop.Emit(OpCodes.Ldarg_0); ingressLoop.Emit(OpCodes.Ret);

        ILGenerator lateTarget = type.DefineMethod("LateTargetAfterHelper", MethodAttributes.Public | MethodAttributes.Static,
            typeof(int), [typeof(int[]), typeof(bool)]).GetILGenerator();
        Label lateTargetLabel = lateTarget.DefineLabel();
        lateTarget.Emit(OpCodes.Ldarg_0); lateTarget.Emit(OpCodes.Ldlen); lateTarget.Emit(OpCodes.Pop);
        lateTarget.Emit(OpCodes.Ldarg_0); lateTarget.Emit(OpCodes.Ldlen); lateTarget.Emit(OpCodes.Pop);
        lateTarget.Emit(OpCodes.Ldarg_1); lateTarget.Emit(OpCodes.Brtrue_S, lateTargetLabel);
        lateTarget.Emit(OpCodes.Ldc_I4_0); lateTarget.Emit(OpCodes.Ret);
        lateTarget.MarkLabel(lateTargetLabel); lateTarget.Emit(OpCodes.Ldc_I4_1); lateTarget.Emit(OpCodes.Ret);

        type.CreateType(); using MemoryStream stream = new MemoryStream(); assembly.Save(stream); byte[] pe = stream.ToArray();
        RestrictedCilImporterV1 importer = new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2);
        ManagedCallGraphCompilationV1 graph = importer.ImportBodyWorld(new(ManagedBodyWorldModeV1.StandaloneRestrictedModule, pe, "raw-starg",
            [new("ArgumentStore", "Choose")], []));
        if (graph.Status != RestrictedCilImportStatusV1.Success)
            throw new Exception("starg.s: " + string.Join(';', graph.Diagnostics.Select(d => d.Code + ":" + d.Message)));
        ManagedCompiledMethodV1 method = graph.Methods.Single();
        if (method.Import.ControlFlowAnalysis?.Phis.SingleOrDefault(phi => phi.SlotIdentity == "arg:0") is null)
            throw new Exception("starg.s branch join must produce an argument-slot phi");
        if (new ScalarControlFlowV2ObjectLinkerV1().Link(graph, graph.Graph!.RootIdentities.Single()).Status !=
            ScalarControlFlowV2LinkStatusV1.Success)
            throw new Exception("starg.s backend/link failed");
        ManagedCallGraphCompilationV1 decrementGraph = importer.ImportBodyWorld(new(ManagedBodyWorldModeV1.StandaloneRestrictedModule, pe,
            "raw-post-decrement", [new("ArgumentStore", "PostDecrement")], []));
        if (decrementGraph.Status != RestrictedCilImportStatusV1.Success ||
            new ScalarControlFlowV2ObjectLinkerV1().Link(decrementGraph, decrementGraph.Graph!.RootIdentities.Single()).Status !=
                ScalarControlFlowV2LinkStatusV1.Success)
            throw new Exception("Loop-carried post-decrement SSA must declare a forward-used value exactly once: " +
                string.Join(';', decrementGraph.Diagnostics.Select(d => d.Code + ":" + d.Message)));
        ManagedCallGraphCompilationV1 twoLatchGraph = importer.ImportBodyWorld(new(ManagedBodyWorldModeV1.StandaloneRestrictedModule, pe,
            "raw-two-latch", [new("ArgumentStore", "TwoLatchLoop")], []));
        ScalarControlFlowV2AnalysisV1? twoLatchAnalysis = twoLatchGraph.Methods.SingleOrDefault()?.Import.ControlFlowAnalysis;
        if (twoLatchGraph.Status != RestrictedCilImportStatusV1.Success || twoLatchAnalysis is null ||
            twoLatchAnalysis.Phis.Any(phi => phi.Incoming.Count !=
                twoLatchAnalysis.Blocks.Single(block => block.Id == phi.BlockId).PredecessorIds.Count))
            throw new Exception("A loop phi must be refreshed when a later reachable latch acquires an exit state: " +
                string.Join(';', twoLatchGraph.Diagnostics.Select(d => d.Code + ":" + d.Message)));
        ManagedCallGraphCompilationV1 ingressGraph = importer.ImportBodyWorld(new(ManagedBodyWorldModeV1.StandaloneRestrictedModule, pe,
            "raw-ingress-loop", [new("ArgumentStore", "IngressLoop")], []));
        IrProgram? ingressProgram = ingressGraph.Methods.SingleOrDefault(method =>
            method.Identity.StableIdentity.Contains("IngressLoop", StringComparison.Ordinal))?.Import.Program;
        int[] ingressCopies = ingressProgram?.Instructions.Where(instruction =>
                instruction.StableIdentity.EndsWith(":argument-copy", StringComparison.Ordinal))
            .Select(instruction => instruction.Index).ToArray() ?? [];
        IrInstruction? backedge = ingressProgram?.Instructions.LastOrDefault(instruction =>
            instruction.Annotation.ResolvedBranchTargetInstructionIndex.HasValue);
        IrRegisterAllocationResultV1? ingressAllocation = null;
        if (ingressProgram is not null)
        {
            IrProgramSchedule ingressSchedule = new HybridCpuLocalListScheduler().ScheduleProgram(ingressProgram);
            IrProgramBundlingResult ingressBundles = new HybridCpuBundleFormer().BundleProgram(ingressSchedule);
            HybridCpuMiiResourceModelV1 resourceModel = (HybridCpuMiiResourceModelV1)typeof(ScalarControlFlowV2ObjectLinkerV1)
                .GetField("ResourceModel", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            ingressAllocation = new HybridCpuScheduleAwareRegisterAllocatorV1().Allocate(
                ingressSchedule, ingressBundles, resourceModel: resourceModel,
                options: HybridCpuRegisterAllocationOptionsV1.Qualification);
        }
        if (ingressGraph.Status != RestrictedCilImportStatusV1.Success || ingressProgram is null ||
            !ingressCopies.SequenceEqual([0, 1]) || backedge?.Annotation.ResolvedBranchTargetInstructionIndex is not >= 2 ||
            ingressAllocation?.Status != IrRegisterAllocationStatusV1.Allocated)
            throw new Exception("CIL IL_0000 backedges must skip the synthetic ABI ingress-copy prolog: " +
                $"import={ingressGraph.Status}; copies={string.Join(',', ingressCopies)}; " +
                $"target={backedge?.Annotation.ResolvedBranchTargetInstructionIndex}; " +
                $"allocation={ingressAllocation?.Status}:{ingressAllocation?.Reason}; " +
                string.Join(';', ingressGraph.Diagnostics.Select(d => d.Code + ":" + d.Message)));
        HybridCpuManagedMetadataArtifactV1 ingressMetadata = new HybridCpuManagedMetadataFinalizerV1()
            .FinalizeRequiredCallSites("ArgumentStore.IngressLoop", 0, ingressAllocation!,
                HybridCpuManagedMetadataOptionsV1.Qualification);
        if (ingressAllocation!.Witness!.Rebuild.MiiRecomputed ||
            ingressMetadata.Status != HybridCpuManagedMetadataStatusV1.Finalized)
            throw new Exception("Exact root maps must accept a final allocation with ordinary-loop fallback; " +
                $"MII is not a GC-map prerequisite: {ingressMetadata.Status}:{ingressMetadata.Reason}");
        RestrictedCilImportResultV1 lateTargetImport = importer.ImportImage(pe, new("ArgumentStore", "LateTargetAfterHelper"));
        IrInstruction? lateBranch = lateTargetImport.Program?.Instructions.SingleOrDefault(instruction =>
            instruction.Annotation.ResolvedBranchTargetInstructionIndex.HasValue &&
            instruction.Annotation.BranchTargetSymbolName?.StartsWith("cil_", StringComparison.Ordinal) == true);
        IrInstruction? resolvedLateTarget = lateBranch is null ? null : lateTargetImport.Program!.Instructions.Single(
            instruction => instruction.Index == lateBranch.Annotation.ResolvedBranchTargetInstructionIndex);
        string expectedTargetOffset = lateBranch?.Annotation.BranchTargetSymbolName?[4..] ?? string.Empty;
        int actualMarker = resolvedLateTarget?.StableIdentity.LastIndexOf(":il_", StringComparison.Ordinal) ?? -1;
        string actualTargetOffset = actualMarker >= 0 ? resolvedLateTarget!.StableIdentity.Substring(actualMarker + 4, 4) : string.Empty;
        if (lateTargetImport.Status != RestrictedCilImportStatusV1.Success || lateBranch is null ||
            resolvedLateTarget is null || !int.TryParse(expectedTargetOffset, System.Globalization.NumberStyles.HexNumber, null, out int expectedOffset) ||
            !int.TryParse(actualTargetOffset, System.Globalization.NumberStyles.HexNumber, null, out int actualOffset) || actualOffset < expectedOffset)
            throw new Exception("A later CIL branch target must be selected by minimum CIL offset, not an earlier helper emission " +
                $"with a synthetic high offset: branch={lateBranch?.StableIdentity}; symbol={lateBranch?.Annotation.BranchTargetSymbolName}; " +
                $"resolved={resolvedLateTarget?.StableIdentity}; diagnostics=" + string.Join(';', lateTargetImport.Diagnostics));
        MethodInfo fixedCallRegister = typeof(RestrictedCilImporterV1).GetMethod(
            "FixedCallRegister", BindingFlags.Static | BindingFlags.NonPublic)!;
        int? helperArgumentRegister = (int?)fixedCallRegister.Invoke(null, ["fixture:null-check-arg-abi:value"]);
        int? helperResultRegister = (int?)fixedCallRegister.Invoke(null, ["fixture:null-check-result-abi:value"]);
        int? intrinsicResultRegister = (int?)fixedCallRegister.Invoke(null, ["fixture:intrinsic:call-result-abi:value"]);
        int? returnRegister = (int?)fixedCallRegister.Invoke(null, ["fixture:return-abi:value"]);
        int? allocationArgument = (int?)fixedCallRegister.Invoke(null, ["fixture:alloc-type-handle-abi:value"]);
        int? allocationResult = (int?)fixedCallRegister.Invoke(null, ["fixture:alloc-result-abi:value"]);
        int? constructorSecondArgument = (int?)fixedCallRegister.Invoke(null, ["fixture:ctor-arg-abi:1:value"]);
        int? malformedRegister = (int?)fixedCallRegister.Invoke(null, ["fixture:arg-abi:value:extra"]);
        if (helperArgumentRegister != 10 || helperResultRegister != 10 || intrinsicResultRegister != 10 ||
            returnRegister != 10 || allocationArgument != 10 || allocationResult != 10 ||
            constructorSecondArgument != 11 || malformedRegister is not null)
            throw new Exception("Exact helper argument/result/return/allocation ABI bridge identities must expose registers before dependency analysis.");
        if (!importer.ImportImage(pe, new("ArgumentStore", "WrongType")).Diagnostics.Any(d => d.Code == "HCCIL1868") ||
            !importer.ImportImage(pe, new("ArgumentStore", "Absent")).Diagnostics.Any(d => d.Code == "HCCIL1866"))
            throw new Exception("starg.s exact type/argument negatives must fail closed");
        if (new RestrictedCilImporterV1().ImportImage(pe, new("ArgumentStore", "Choose")).Status == RestrictedCilImportStatusV1.Success)
            throw new Exception("Frozen V1 profile must not acquire starg.s implicitly");
        Console.WriteLine("PASS mutable argument-slot SSA, branch phi, backend link, exact negatives and frozen-v1 isolation");
    }
}
