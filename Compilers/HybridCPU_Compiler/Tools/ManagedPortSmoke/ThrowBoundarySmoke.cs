using System.Reflection;
using System.Reflection.Emit;
using HybridCPU.Compiler.Cil;
using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.IR.Resources;
using HybridCPU.Compiler.Core.Target;
using HybridCPU.Compiler.Core.Target.Managed;
using HybridCPU.Compiler.Core.Target.Object;

internal static class ThrowBoundarySmoke
{
    public static void Run()
    {
        PersistedAssemblyBuilder assembly = new PersistedAssemblyBuilder(new AssemblyName("ThrowBoundaryFixture"), typeof(object).Assembly);
        TypeBuilder type = assembly.DefineDynamicModule("Main").DefineType("Fixture", TypeAttributes.Public);
        ILGenerator Method(string name) => type.DefineMethod(name, MethodAttributes.Public | MethodAttributes.Static,
            typeof(void), Type.EmptyTypes).GetILGenerator();
        ILGenerator il = Method("ThrowNull"); il.Emit(OpCodes.Ldnull); il.Emit(OpCodes.Throw);
        il = Method("WrongType"); il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Throw);
        il = Method("Underflow"); il.Emit(OpCodes.Throw);
        il = Method("Falloff"); il.Emit(OpCodes.Nop);
        il = Method("Return"); il.Emit(OpCodes.Ret);
        type.CreateType();
        using MemoryStream stream = new MemoryStream(); assembly.Save(stream);
        RestrictedCilImporterV1 importer = new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2);
        foreach ((string? method, string? code) in new[] { ("WrongType", "HCCIL0883"),
                     ("Underflow", "HCCIL0882"), ("Falloff", "HCCIL1101") })
        {
            RestrictedCilImportResultV1 result = importer.ImportImage(stream.ToArray(), new("Fixture", method));
            if (result.Program is not null || !result.Diagnostics.Any(d => d.Code == code))
                throw new Exception(method + ": " + string.Join(';', result.Diagnostics));
        }
        if (importer.ImportImage(stream.ToArray(), new("Fixture", "Return")).Status != RestrictedCilImportStatusV1.Success)
            throw new Exception("Ordinary return must remain supported");
        RestrictedCilImportResultV1 throwing = importer.ImportImage(stream.ToArray(), new("Fixture", "ThrowNull"));
        if (throwing.Status != RestrictedCilImportStatusV1.Success || !throwing.RequiresNativeExceptionTransfer ||
            !throwing.Program!.Instructions.Any(i => i.Annotation.BranchTargetSymbolName == "__hybridcpu_managed_throw"))
            throw new Exception("throw must lower to the exact runtime call and retain native qualification requirement: " + string.Join(';', throwing.Diagnostics));
        ManagedCallGraphCompilationV1 graph = importer.ImportBodyWorld(new(ManagedBodyWorldModeV1.StandaloneRestrictedModule, stream.ToArray(),
            "throw-boundary", [new("Fixture", "ThrowNull")], []));
        if (graph.Status != RestrictedCilImportStatusV1.Success)
            throw new Exception("throw graph: " + string.Join(';', graph.Diagnostics));
        ScalarControlFlowV2LinkedProgramV1 missingMetadata = new ScalarControlFlowV2ObjectLinkerV1().Link(graph, graph.Graph!.RootIdentities.Single());
        if (missingMetadata.Status != ScalarControlFlowV2LinkStatusV1.BackendRejected ||
            !missingMetadata.Diagnostics.Any(d => d.Code == "HCSCF-LINK4061") ||
            missingMetadata.LinkedImage is not null || missingMetadata.RestrictedImage is not null ||
            missingMetadata.MethodObjects.Count != 0)
            throw new Exception("Native EH must reject a missing exception descriptor before object emission.");
        graph = importer.ImportBodyWorld(new(ManagedBodyWorldModeV1.StandaloneRestrictedModule, stream.ToArray(),
            "throw-boundary-with-metadata", [new("Fixture", "ThrowNull")], [],
            MetadataOnlyModules: [new(File.ReadAllBytes(typeof(object).Assembly.Location), "exact-corelib-metadata")]));
        if (graph.Status != RestrictedCilImportStatusV1.Success)
            throw new Exception("throw metadata graph: " + string.Join(';', graph.Diagnostics));
        ScalarControlFlowV2LinkedProgramV1 linked = new ScalarControlFlowV2ObjectLinkerV1().Link(graph, graph.Graph!.RootIdentities.Single());
        if (linked.Status != ScalarControlFlowV2LinkStatusV1.Success || linked.RestrictedImage is not { RuntimeBootstrap: { } bootstrap } ||
            bootstrap.StaticRoots.All(root => root.Identity != HybridCpuManagedEhStateObjectV1.RootSymbol) ||
            linked.LinkedImage?.Symbols.Count(symbol => symbol.Name == HybridCpuManagedEhCatchDispatchEmitterV1.ThrowSymbol) != 1)
            throw new Exception("Catch-only native dispatch must link with its exact helper, bootstrap, and exception root: " +
                string.Join(';', linked.Diagnostics));
        ManagedWorkstreamDiscoveryV1 workstreams = ManagedWorkstreamDiscoveryContractV1.Discover(graph, linked);
        if (!workstreams.IsComplete ||
            !workstreams.RequiredWorkstreams.Contains("eh-unwind", StringComparer.Ordinal) ||
            !workstreams.Evidence.Any(static row => row.Contains("native-eh-method-record", StringComparison.Ordinal)) ||
            !workstreams.Evidence.Contains(
                "eh-unwind:runtime-hco=hybridcpu.managed-runtime.endfinally/v1",
                StringComparer.Ordinal))
            throw new Exception("Post-link workstream discovery must derive EH from native method/runtime evidence: " +
                $"required={string.Join(',', workstreams.RequiredWorkstreams)}; " +
                $"unclassified={string.Join(',', workstreams.UnclassifiedRuntimeModules)}");
        // Isolated backend evidence with the exact external helper declared; this does not
        // authorize its implementation or bypass the public image linker gate above.
        ManagedCompiledMethodV1 methodForObject = graph.Methods.Single() with { DirectCalleeIdentities = ["__hybridcpu_managed_throw"] };
        object? compiled = typeof(ScalarControlFlowV2ObjectLinkerV1).GetMethod("CompileMethod", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, new object[] { methodForObject, 0 });
        if (compiled is not ScalarControlFlowV2MethodObjectV1 { FrameSizeBytes: > 0 } methodObject ||
            methodObject.ObjectArtifact.Status != HybridCpuObjectStatusV1.Success)
            throw new Exception("Exact throw frame must compile without discarding caller unwind state");
        IrProgramSchedule schedule = new HybridCpuLocalListScheduler().ScheduleProgram(throwing.Program!);
        IrRegisterAllocationResultV1 allocation = new HybridCpuScheduleAwareRegisterAllocatorV1().Allocate(schedule,
            new HybridCpuBundleFormer().BundleProgram(schedule), resourceModel:
                (HybridCpuMiiResourceModelV1)typeof(ScalarControlFlowV2ObjectLinkerV1)
                    .GetField("ResourceModel", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!,
            options: HybridCpuRegisterAllocationOptionsV1.Qualification);
        if (allocation.Status != IrRegisterAllocationStatusV1.Allocated || allocation.Witness is not { } witness ||
            !witness.Frame.Slots.Any(slot => slot.Identity == "saved:x1") ||
            !witness.Mutations.Any(mutation => mutation.Kind == IrAllocationMutationKindV1.CalleeSave) ||
            witness.Mutations.Any(mutation => mutation.Kind is IrAllocationMutationKindV1.Epilogue or IrAllocationMutationKindV1.CalleeRestore))
            throw new Exception("Throw path must save the caller return address and retain the frame without an epilogue.");
        HybridCpuManagedUnwindRecordV2 unwind = HybridCpuManagedUnwindCodecV2.Decode(
            methodObject.ObjectArtifact.Sections.Single(section => section.Name == ".hcunwind").Data);
        IrProgramBundlingResult homeBundles = new HybridCpuBundleFormer().BundleProgram(schedule);
        HybridCpuMiiResourceModelV1 homeResources = (HybridCpuMiiResourceModelV1)typeof(ScalarControlFlowV2ObjectLinkerV1)
            .GetField("ResourceModel", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        IrRegisterAllocationResultV1 withHomes = new HybridCpuScheduleAwareRegisterAllocatorV1().Allocate(schedule, homeBundles,
            resourceModel: homeResources, options: HybridCpuRegisterAllocationOptionsV1.Qualification,
            fixedFrameSlots: [new("eh-home:local:0", 8, 8)]);
        if (withHomes.Status != IrRegisterAllocationStatusV1.Allocated || withHomes.Witness is not { } homeWitness ||
            !homeWitness.Frame.Slots.Any(slot => slot.Identity == "eh-home:local:0" && slot.SizeBytes == 8 &&
                slot.OffsetFromAdjustedStackPointerBytes % 8 == 0) ||
            homeWitness.Frame.Slots.Select(slot => slot.OffsetFromAdjustedStackPointerBytes).Distinct().Count() != homeWitness.Frame.Slots.Count ||
            homeWitness.Frame.Digest == witness.Frame.Digest)
            throw new Exception("Fixed EH homes must coexist with saved registers in the real allocation frame witness.");
        IrRegisterAllocationResultV1 invalidHomes = new HybridCpuScheduleAwareRegisterAllocatorV1().Allocate(schedule, homeBundles,
            resourceModel: homeResources, options: HybridCpuRegisterAllocationOptionsV1.Qualification,
            fixedFrameSlots: [new("saved:x1", 8, 8)]);
        if (invalidHomes.Status != IrRegisterAllocationStatusV1.InvalidInput || invalidHomes.Witness is not null)
            throw new Exception("Caller frame reservations must not alias allocator-owned saved registers.");
        HybridCpuScheduleAwareRegisterAllocatorV1 frameAllocator = new HybridCpuScheduleAwareRegisterAllocatorV1();
        HybridCpuFrameSlotV2 homeSlot = homeWitness.Frame.Slots.Single(slot => slot.Identity == "eh-home:local:0");
        IrInstruction homeStore = frameAllocator.CreateFixedFrameAccess(withHomes, throwing.Program!.Instructions[0],
            homeSlot.Identity, false, 10, "eh:test:store");
        IrInstruction homeLoad = frameAllocator.CreateFixedFrameAccess(withHomes, throwing.Program.Instructions[0],
            homeSlot.Identity, true, 11, "eh:test:load");
        if (homeStore.Opcode != HybridCpuOpcode.SD || homeLoad.Opcode != HybridCpuOpcode.LD ||
            homeStore.Immediate != homeSlot.OffsetFromAdjustedStackPointerBytes || homeLoad.Immediate != homeStore.Immediate ||
            homeStore.Annotation.MemoryWriteRegion is not { Length: 8, IsWrite: true } ||
            homeLoad.Annotation.MemoryReadRegion is not { Length: 8, IsWrite: false } ||
            !homeStore.Annotation.Uses.Any(operand => operand.Kind == IrOperandKind.ArchitecturalRegister && operand.Value == 2) ||
            !homeLoad.Annotation.Defs.Any(operand => operand.Value == 11))
            throw new Exception("Fixed home access must use ordinary LD/SD with exact final SP-relative displacement and memory effects.");
        foreach ((string? slotName, bool load, int register) in new[] { ("missing", true, 10), ("saved:x1", true, 10),
                     (homeSlot.Identity, true, 2), (homeSlot.Identity, true, 0) })
        {
            bool rejected = false;
            try { frameAllocator.CreateFixedFrameAccess(withHomes, throwing.Program.Instructions[0], slotName, load, register, "bad"); }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException) { rejected = true; }
            if (!rejected) throw new Exception("Invalid fixed-home access must fail closed before emission.");
        }
        int savedReturnOffset = witness.Frame.Slots.Single(slot => slot.Identity == "saved:x1")
            .OffsetFromAdjustedStackPointerBytes - witness.Frame.FrameSizeBytes;
        if (unwind.CfaOffsetBytes != witness.Frame.FrameSizeBytes || unwind.ReturnPcRegisterId is not null ||
            unwind.ReturnPcCfaRelativeOffsetBytes != savedReturnOffset ||
            !unwind.SavedRegisters.Any(row => row.RegisterId == 1 && row.CfaRelativeOffsetBytes == savedReturnOffset))
            throw new Exception("Throw HCO unwind record must describe the exact saved caller return address.");
        ManagedCallGraphCompilationV1 leafGraph = importer.ImportBodyWorld(new(ManagedBodyWorldModeV1.StandaloneRestrictedModule, stream.ToArray(),
            "return-boundary", [new("Fixture", "Return")], []));
        ScalarControlFlowV2LinkedProgramV1 leafLinked = new ScalarControlFlowV2ObjectLinkerV1().Link(leafGraph, leafGraph.Graph!.RootIdentities.Single());
        if (leafLinked.Status != ScalarControlFlowV2LinkStatusV1.Success)
            throw new Exception("Handler-free leaf unwind metadata must retain ordinary linking.");
        ScalarControlFlowV2MethodObjectV1 leafObject = leafLinked.MethodObjects.Single();
        MethodInfo lowerWord = typeof(HybridCpuBundleLowerer).GetMethod("LowerInstruction", BindingFlags.NonPublic | BindingFlags.Static)!;
        HybridCpuInstructionWord Word(IrInstruction instruction) => (HybridCpuInstructionWord)lowerWord.Invoke(null, new object[] { instruction })!;
        IrProgram native = new HybridCpuIrBuilder().BuildProgram(0, new[] { Word(homeStore), Word(homeLoad),
            Word(leafGraph.Methods.Single().Import.Program!.Instructions.Last()) });
        IrInstruction[] symbolicInstructions = native.Instructions.ToArray();
        symbolicInstructions[^1] = leafGraph.Methods.Single().Import.Program!.Instructions.Last();
        symbolicInstructions[0] = frameAllocator.CreateSymbolicFixedFrameAccess(native.Instructions[0], homeSlot.Identity,
            false, new(IrOperandKind.ArchitecturalRegister, 10, "rs2"), "fixed:store");
        symbolicInstructions[1] = frameAllocator.CreateSymbolicFixedFrameAccess(native.Instructions[1], homeSlot.Identity,
            true, new(IrOperandKind.ArchitecturalRegister, 11, "rd"), "fixed:load");
        for (int index = 0; index < symbolicInstructions.Length; index++)
            symbolicInstructions[index] = symbolicInstructions[index] with { Index = index, EncodedAddress = (ulong)index * 32 };
        IrProgram symbolic = native with { Instructions = symbolicInstructions, ControlFlowGraph = native.ControlFlowGraph with
        { Blocks = native.BasicBlocks.Select(block => block with
            { Instructions = symbolicInstructions.Where(instruction => instruction.Index >= block.StartInstructionIndex &&
                instruction.Index <= block.EndInstructionIndex).ToArray() }).ToArray() } };
        IrProgramSchedule symbolicSchedule = new HybridCpuLocalListScheduler().ScheduleProgram(symbolic);
        IrProgramBundlingResult symbolicBundles = new HybridCpuBundleFormer().BundleProgram(symbolicSchedule);
        bool unresolvedRejected = false;
        try { new HybridCpuBundleLowerer().LowerProgram(symbolicBundles); }
        catch (InvalidOperationException) { unresolvedRejected = true; }
        if (!unresolvedRejected) throw new Exception("An unresolved frame symbol must never encode as offset zero.");
        IrRegisterAllocationResultV1 resolved = frameAllocator.Allocate(symbolicSchedule, symbolicBundles, resourceModel: homeResources,
            options: HybridCpuRegisterAllocationOptionsV1.Qualification,
            fixedFrameSlots: [new("a-padding", 16, 8), new(homeSlot.Identity, 8, 8)]);
        if (resolved.Status != IrRegisterAllocationStatusV1.Allocated || resolved.Witness is null)
            throw new Exception("Symbolic frame pipeline: " + resolved.Status + ": " + resolved.Reason);
        int resolvedOffset = resolved.Witness.Frame.Slots.Single(slot => slot.Identity == homeSlot.Identity).OffsetFromAdjustedStackPointerBytes;
        IrInstruction[] resolvedAccesses = resolved.FinalSchedule.Program.Instructions.Where(instruction => instruction.StableIdentity.StartsWith("fixed:", StringComparison.Ordinal)).ToArray();
        if (resolvedAccesses.Length != 2 || resolvedOffset == 0 || resolvedAccesses.Any(instruction =>
            instruction.Annotation.FixedFrameSlotIdentity is not null || instruction.Immediate != resolvedOffset))
            throw new Exception("RA must resolve both symbolic accesses to the actual shared frame slot.");
        new HybridCpuBundleLowerer().LowerProgram(resolved.FinalBundles);
        HybridCpuManagedUnwindRecordV2 leafUnwind = HybridCpuManagedUnwindCodecV2.Decode(
            leafObject.ObjectArtifact.Sections.Single(section => section.Name == ".hcunwind").Data);
        if (leafUnwind.CfaOffsetBytes != 0 || leafUnwind.ReturnPcRegisterId != 1 ||
            leafUnwind.ReturnPcCfaRelativeOffsetBytes is not null || leafUnwind.SavedRegisters.Count != 0)
            throw new Exception("Frameless leaf unwind must recover the caller PC from x1, not a fabricated stack slot.");
        ScalarControlFlowV2LinkedProgramV1 repeatedLeaf = new ScalarControlFlowV2ObjectLinkerV1().Link(leafGraph, leafGraph.Graph!.RootIdentities.Single());
        if (leafObject.ObjectArtifact.ObjectSha256 != repeatedLeaf.MethodObjects.Single().ObjectArtifact.ObjectSha256)
            throw new Exception("Frame metadata must preserve deterministic HCO output.");
        Console.WriteLine("PASS typed throw runtime-call lowering and explicit native/image gate; no trap substitution");
    }
}
