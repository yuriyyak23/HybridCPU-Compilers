using System.Buffers.Binary;
using HybridCPU.Compiler.Cil;
using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.IR.Resources;
using HybridCPU.Compiler.Core.Target;
using HybridCPU.Compiler.Core.Target.Link;
using HybridCPU.Compiler.Core.Target.Managed;
using HybridCPU.Compiler.Core.Target.Object;
using HybridCPU.Compiler.Core.Target.Runtime;
using HybridCPU.ManagedRuntime;
using HybridCPU.Platform.Contracts;
using HybridCPU.RefPlan7.Phase00.Corpus;
using HybridCPU.RuntimeKernel;
using HybridCPU_ISE.Arch;
using HybridCPU_ISE.Core;
using YAKSys_Hybrid_CPU;

namespace HybridCPU_ISE.Tests.CompilerTests;

public sealed class CompilerRefPlan7Phase06ManagedDispatchTests
{
    private const string Signature = "object-ref,int32->int32";
    private const string InterfaceMethod = "IDispatchCorpus.Invoke";
    private const string BaseMethod = "DispatchBase.Invoke";
    private const string DerivedMethod = "DispatchDerived.Invoke";
    private const string OtherMethod = "DispatchOther.Invoke";
    private static readonly byte[] FixtureImage = File.ReadAllBytes(typeof(DispatchCaller).Assembly.Location);
    private static readonly HybridCpuMiiResourceModelV1 ResourceModel =
        HybridCpuMiiResourceModelV1.Create(new HybridCpuMachineTopologyV1(64, 64, 4, 8, 2, 16), 8);

    [Fact]
    public void RuntimeTable_ResolvesOverrideInterfaceAndInheritedImplementationDeterministically()
    {
        Subject first = CreateSubject();
        Subject second = CreateSubject();
        Assert.Equal(first.Table.Digest, second.Table.Digest);
        Assert.Equal(first.Table.VirtualEntries, second.Table.VirtualEntries);
        Assert.Equal(first.Table.InterfaceEntries, second.Table.InterfaceEntries);

        ulong baseObject = Allocate(first, typeof(DispatchBase).FullName!);
        ulong derivedObject = Allocate(first, typeof(DispatchDerived).FullName!);
        ulong otherObject = Allocate(first, typeof(DispatchOther).FullName!);
        Assert.Equal(0x1000UL, first.Runtime.ResolveVirtual(baseObject, first.VirtualSlot).CodeAddress);
        Assert.Equal(0x2000UL, first.Runtime.ResolveVirtual(derivedObject, first.VirtualSlot).CodeAddress);
        Assert.Equal(0x2000UL, first.Runtime.ResolveInterface(derivedObject, first.InterfaceTypeId, first.InterfaceSlot).CodeAddress);
        Assert.Equal(0x3000UL, first.Runtime.ResolveInterface(otherObject, first.InterfaceTypeId, first.InterfaceSlot).CodeAddress);
    }

    [Fact]
    public void RuntimeDispatch_NullCastMissingSlotAndCorruptMetadataFailClosed()
    {
        Subject subject = CreateSubject();
        Assert.Equal(HybridCpuManagedDispatchStatusV1.NullReceiver,
            subject.Runtime.ResolveVirtual(0, subject.VirtualSlot).Status);
        ulong baseObject = Allocate(subject, typeof(DispatchBase).FullName!);
        Assert.Equal(HybridCpuManagedDispatchStatusV1.InvalidCast,
            subject.Runtime.CastClass(baseObject, Type(subject, typeof(DispatchDerived).FullName!).TypeId).Status);
        Assert.Equal(HybridCpuManagedDispatchStatusV1.MissingSlot,
            subject.Runtime.ResolveVirtual(baseObject, 0xdead).Status);

        HybridCpuManagedDispatchTableBuildV1 corrupt = new HybridCpuManagedDispatchTableBuilderV1().Build(subject.Types,
            [new(BaseMethod, Type(subject, typeof(DispatchBase).FullName!).TypeId, Signature, 0x1000, 0, true, false, 0xdead)], []);
        Assert.Equal(HybridCpuManagedDispatchStatusV1.InvalidMetadata, corrupt.Status);
        Assert.Null(corrupt.Table);
    }

    [Fact]
    public void CastAndIsInst_PreserveNullAndUseRuntimeAssignability()
    {
        Subject subject = CreateSubject();
        ulong derived = Allocate(subject, typeof(DispatchDerived).FullName!);
        ulong target = Type(subject, typeof(DispatchDerived).FullName!).TypeId;
        Assert.Equal(0UL, subject.Runtime.CastClass(0, target).ObjectReference);
        Assert.Equal(derived, subject.Runtime.CastClass(derived, target).ObjectReference);
        Assert.Equal(derived, subject.Runtime.IsInstance(derived, subject.InterfaceTypeId).ObjectReference);
        ulong other = Allocate(subject, typeof(DispatchOther).FullName!);
        Assert.Equal(0UL, subject.Runtime.IsInstance(other, target).ObjectReference);
    }

    [Fact]
    public void CilCallvirt_LowersResolverThenRelocationFreeGenericJalr()
    {
        Subject subject = CreateSubject();
        RestrictedCilImportResultV1 import = Import(nameof(DispatchCaller.VirtualCall), subject, virtualDispatch: true);
        Assert.True(import.Status == RestrictedCilImportStatusV1.Success,
            string.Join(';', import.Diagnostics.Select(static row => $"{row.Code}:{row.Message}")));
        IrInstruction[] calls = import.Program!.Instructions
            .Where(static row => row.Annotation.ControlFlowKind == IrControlFlowKind.Call).ToArray();
        Assert.Contains(calls, static row =>
            row.Annotation.BranchTargetSymbolName == "__hybridcpu_managed_null_check");
        Assert.Contains(calls, static row =>
            row.Annotation.BranchTargetSymbolName == "__hybridcpu_managed_resolve_virtual");
        IrInstruction[] indirectCalls = calls.Where(static row => row.Opcode == HybridCpuOpcode.JALR &&
            row.Annotation.BranchTargetSymbolName is null).ToArray();
        Assert.NotEmpty(indirectCalls);
        Assert.All(indirectCalls, static indirect =>
        {
            Assert.Null(indirect.Annotation.BranchTargetSymbolName);
            Assert.Equal(0, indirect.Immediate);
        });

        IrRegisterAllocationResultV1 allocation = Allocate(import.Program);
        Assert.True(allocation.Status == IrRegisterAllocationStatusV1.Allocated,
            $"{allocation.Status}: {allocation.Reason}");
        IrInstruction finalIndirect = allocation.FinalSchedule.Program.Instructions.Single(static row =>
            row.Opcode == HybridCpuOpcode.JALR && row.Annotation.ControlFlowKind == IrControlFlowKind.Call &&
            row.Annotation.BranchTargetSymbolName is null);
        Assert.True(finalIndirect.Operands.Any(static operand => operand.Kind == IrOperandKind.ArchitecturalRegister && operand.Value == 5),
            string.Join(';', finalIndirect.Operands.Select(static operand => $"{operand.Kind}:{operand.Name}:{operand.Value}")));
        IReadOnlyList<HybridCpuInstructionBundle> lowered = new HybridCpuBundleLowerer().LowerProgram(allocation.FinalBundles);
        HybridCpuInstructionWord[] carriers = lowered.SelectMany(static bundle =>
                Enumerable.Range(0, HybridCpuInstructionBundle.SlotCount).Select(bundle.GetInstruction))
            .Where(static row => (HybridCpuOpcode)row.OpCode == HybridCpuOpcode.JALR && row.Immediate == 0)
            .ToArray();
        Assert.Contains(carriers, static carrier =>
            HybridCpuInstructionWord.TryUnpackArchRegs(carrier.Word1, out byte rd, out byte rs1, out byte rs2) &&
            rd == HybridCpuNativeAbiContractV2.ReturnAddressRegister && rs1 == 5 &&
            rs2 == HybridCpuInstructionWord.NoArchReg);
    }

    [Fact]
    public void ResolverAndIndirectCallBothCarryFinalPreciseReceiverRoots()
    {
        Subject subject = CreateSubject();
        RestrictedCilImportResultV1 import = Import(nameof(DispatchCaller.VirtualCall), subject, virtualDispatch: true);
        IrRegisterAllocationResultV1 allocation = Allocate(import.Program!);
        Assert.Equal(IrRegisterAllocationStatusV1.Allocated, allocation.Status);
        HybridCpuManagedMetadataArtifactV1 maps = new HybridCpuManagedMetadataFinalizerV1()
            .FinalizeRequiredCallSites("Phase06.VirtualCall", 0, allocation,
                HybridCpuManagedMetadataOptionsV1.Qualification);
        Assert.True(maps.Status == HybridCpuManagedMetadataStatusV1.Finalized, $"{maps.Status}: {maps.Reason}");
        int requiredSafepoints = allocation.OriginalSchedule!.Program.Instructions.Count(static row =>
            row.Annotation.IsManagedGcSafepoint &&
            (row.Annotation.ControlFlowKind == IrControlFlowKind.Call ||
             row.SideEffects.ArchitecturalEffects.HasFlag(IrArchitecturalEffectKind.Call)));
        Assert.Equal(requiredSafepoints, maps.Safepoints.Count);
        Assert.True(requiredSafepoints >= 3);
        Assert.All(maps.Safepoints, static point =>
            Assert.Contains(point.LiveReferences, static root => root.ReferenceKind == HybridCpuGcReferenceKindV1.ObjectReference));
        Assert.Equal(maps.ResultDigest, new HybridCpuManagedMetadataFinalizerV1()
            .FinalizeRequiredCallSites("Phase06.VirtualCall", 0, allocation,
                HybridCpuManagedMetadataOptionsV1.Qualification).ResultDigest);
    }

    [Fact]
    public void IndirectCall_UnderRegisterPressurePreservesJalrAndFinalReceiverRoot()
    {
        string[] roots = Enumerable.Range(0, 36).Select(static index => $"dispatch-root-{index:D2}").ToArray();
        CompilerRefPlan5Phase25AManagedMetadataFinalizationTests.AllocatedSubject subject =
            CompilerRefPlan5Phase25AManagedMetadataFinalizationTests.AllocateIndirectCallSubject(roots);
        IrRegisterAllocationResultV1 allocation = subject.Result;
        Assert.Equal(IrRegisterAllocationStatusV1.Allocated, allocation.Status);
        Assert.NotEmpty(allocation.Witness!.Spills);
        Assert.Contains(allocation.FinalSchedule.Program.Instructions, static row =>
            row.Opcode == HybridCpuOpcode.JALR && row.Annotation.ControlFlowKind == IrControlFlowKind.Call &&
            row.Operands.Any(static operand => operand.Kind == IrOperandKind.ArchitecturalRegister && operand.Value == 5));
        HybridCpuManagedMetadataArtifactV1 maps = new HybridCpuManagedMetadataFinalizerV1()
            .FinalizeRequiredCallSites("Phase06.IndirectPressure", 0, allocation,
                HybridCpuManagedMetadataOptionsV1.Qualification);
        Assert.True(maps.Status == HybridCpuManagedMetadataStatusV1.Finalized, $"{maps.Status}: {maps.Reason}");
        Assert.All(maps.Safepoints, static point => Assert.Contains(point.LiveReferences,
            static root => root.ReferenceKind == HybridCpuGcReferenceKindV1.ObjectReference));
    }

    [Fact]
    public void RuntimeResolvedAddressExecutesThroughExistingIseJalr()
    {
        Subject subject = CreateSubject();
        ulong receiver = Allocate(subject, typeof(DispatchDerived).FullName!);
        HybridCpuManagedDispatchResultV1 resolved = subject.Runtime.ResolveVirtual(receiver, subject.VirtualSlot);
        Assert.True(resolved.IsSuccess, resolved.Reason);
        Processor.MultiBankMemoryArea memory = new Processor.MultiBankMemoryArea(4, 0x10000UL);
        Processor.CPU_Core core = new Processor.CPU_Core(0, CpuCorePlatformContext.CreateFixed(memory, ProcessorMode.Compiler));
        core.InitializePipeline();
        core.PrepareExecutionStart(0x400);
        TraceSink trace = new TraceSink(TraceFormat.JSON, "phase06-runtime-dispatch.trace.json");
        trace.SetEnabled(true);
        trace.SetLevel(TraceLevel.Full);
        TraceSink? priorTrace = Processor.TraceSink;
        Processor.TraceSink = trace;
        try
        {
        core.WriteCommittedArch(0, 5, resolved.CodeAddress);
        core.WriteCommittedArch(0, HybridCpuNativeAbiContractV2.Default.ArgumentRegisters[0], receiver);
            VLIW_Instruction carrier = new VLIW_Instruction
        {
            OpCode = (uint)Processor.CPU_Core.InstructionsEnum.JALR,
            DataTypeValue = DataTypeEnum.INT64,
            PredicateMask = byte.MaxValue,
            DestSrc1Pointer = HybridCpuInstructionWord.PackArchRegs(
                (byte)HybridCpuNativeAbiContractV2.ReturnAddressRegister, 5, HybridCpuInstructionWord.NoArchReg)
        };
        CompilerRefPlan7Phase03ManagedHeapAllocatorTests.RetireBundle(core,
            [carrier, .. Enumerable.Repeat(new VLIW_Instruction(), 7)], 0x400);
        Assert.Equal(resolved.CodeAddress, core.ReadCommittedPc(0));
        Assert.Equal(receiver, core.ReadArch(0, HybridCpuNativeAbiContractV2.Default.ArgumentRegisters[0]));
        Assert.Equal(0x404UL, core.ReadArch(0, HybridCpuNativeAbiContractV2.ReturnAddressRegister));
        // Retire publication must preserve the ordinary instance receiver in x10.
        FullStateTraceEvent? published = trace.GetThreadTrace(0).LastOrDefault();
        if (published is not null)
            Assert.Equal(receiver, published.Value.RegisterFile![HybridCpuNativeAbiContractV2.Default.ArgumentRegisters[0]]);
        }
        finally
        {
            Processor.TraceSink = priorTrace;
        }
    }

    [Fact]
    public void CilInterfaceAndTypeTests_RequireExactBindings()
    {
        Subject subject = CreateSubject();
        Assert.Equal(RestrictedCilImportStatusV1.Success,
            Import(nameof(DispatchCaller.InterfaceCall), subject, virtualDispatch: false).Status);
        Assert.Equal(RestrictedCilImportStatusV1.Success, Import(nameof(DispatchCaller.Cast), subject, null).Status);
        Assert.Equal(RestrictedCilImportStatusV1.Success, Import(nameof(DispatchCaller.Test), subject, null).Status);

        RestrictedCilImporterV1 missing = new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2);
        RestrictedCilImportResultV1 rejected = missing.ImportImage(FixtureImage,
            new(typeof(DispatchCaller).FullName!, nameof(DispatchCaller.VirtualCall)), "phase06-missing-binding");
        Assert.Equal(RestrictedCilImportStatusV1.Unsupported, rejected.Status);
        Assert.Equal("HCCIL1501", Assert.Single(rejected.Diagnostics).Code);
    }

    [Fact]
    public void BodyWorld_UsesExactDispatchCandidatesForReachabilityAndRejectsAnEmptySet()
    {
        Subject subject = CreateSubject();
        int callToken = typeof(DispatchBase).GetMethod(nameof(DispatchBase.Invoke))!.MetadataToken;
        int baseToken = callToken;
        int derivedToken = typeof(DispatchDerived).GetMethod(nameof(DispatchDerived.Invoke))!.MetadataToken;
        RestrictedCilDispatchBindingV1 binding = new(callToken, BaseMethod,
            [RestrictedCilTypeV1.ObjectReference, RestrictedCilTypeV1.Int32], RestrictedCilTypeV1.Int32,
            RestrictedCilDispatchKindV1.Virtual, subject.VirtualSlot, null, [baseToken, derivedToken]);
        RestrictedCilImporterV1 importer = new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2,
            dispatchBindings: [binding]);
        ManagedCallGraphCompilationV1 graph = importer.ImportBodyWorld(new(
            ManagedBodyWorldModeV1.StandaloneRestrictedModule, FixtureImage, "phase06-dispatch-world",
            [new(typeof(DispatchCaller).FullName!, nameof(DispatchCaller.VirtualCall))], []));
        Assert.Equal(RestrictedCilImportStatusV1.Success, graph.Status);
        Assert.Equal(3, graph.Methods.Count);
        Assert.Equal(2, graph.Graph!.Edges.Count(static edge => edge.IsDispatchCandidate));
        Assert.Contains(graph.Methods, static row => row.Identity.MethodName == nameof(DispatchBase.Invoke));
        Assert.Contains(graph.Methods, static row => row.Identity.DeclaringType.EndsWith(nameof(DispatchDerived), StringComparison.Ordinal));

        RestrictedCilImporterV1 empty = new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2,
            dispatchBindings: [binding with { CandidateMethodMetadataTokens = [] }]);
        ManagedCallGraphCompilationV1 rejected = empty.ImportBodyWorld(new(
            ManagedBodyWorldModeV1.StandaloneRestrictedModule, FixtureImage, "phase06-empty-dispatch-world",
            [new(typeof(DispatchCaller).FullName!, nameof(DispatchCaller.VirtualCall))], []));
        Assert.Equal(RestrictedCilImportStatusV1.Unsupported, rejected.Status);
        Assert.Equal("HCSCF-DISPATCH1001", Assert.Single(rejected.Diagnostics).Code);

        RestrictedCilImporterV1 mismatched = new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2,
            dispatchBindings: [binding with { CandidateMethodMetadataTokens =
                [typeof(DispatchCaller).GetMethod(nameof(DispatchCaller.Cast))!.MetadataToken] }]);
        ManagedCallGraphCompilationV1 mismatchedResult = mismatched.ImportBodyWorld(new(
            ManagedBodyWorldModeV1.StandaloneRestrictedModule, FixtureImage, "phase06-mismatched-dispatch-world",
            [new(typeof(DispatchCaller).FullName!, nameof(DispatchCaller.VirtualCall))], []));
        Assert.Equal(RestrictedCilImportStatusV1.Unsupported, mismatchedResult.Status);
        Assert.Equal("HCSCF-DISPATCH1003", Assert.Single(mismatchedResult.Diagnostics).Code);
    }

    [Fact]
    public void BoundedRecursiveBodyWorld_PreservesExactDispatchCandidatesAndIndirectLowering()
    {
        Subject subject = CreateSubject();
        int callToken = typeof(DispatchBase).GetMethod(nameof(DispatchBase.Invoke))!.MetadataToken;
        RestrictedCilDispatchBindingV1 binding = new(callToken, BaseMethod,
            [RestrictedCilTypeV1.ObjectReference, RestrictedCilTypeV1.Int32], RestrictedCilTypeV1.Int32,
            RestrictedCilDispatchKindV1.Virtual, subject.VirtualSlot, null,
            [callToken, typeof(DispatchDerived).GetMethod(nameof(DispatchDerived.Invoke))!.MetadataToken]);
        RestrictedCilImporterV1 importer = new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2,
            dispatchBindings: [binding]);
        ManagedCallGraphCompilationV1 graph = importer.ImportBodyWorld(new(
            ManagedBodyWorldModeV1.StandaloneRestrictedModule, FixtureImage, "phase06-recursive-dispatch",
            [new(typeof(DispatchCaller).FullName!, nameof(DispatchCaller.DispatchWithRecursionRoot))],
            [new(typeof(DispatchCaller).FullName!, nameof(DispatchCaller.DispatchWithRecursionRoot)),
                new(typeof(DispatchCaller).FullName!, nameof(DispatchCaller.Countdown)),
                new(typeof(DispatchBase).FullName!, nameof(DispatchBase.Invoke)),
                new(typeof(DispatchDerived).FullName!, nameof(DispatchDerived.Invoke))],
            ManagedBoundedRecursionOptionsV1.Qualification));
        Assert.True(graph.Status == RestrictedCilImportStatusV1.Success,
            string.Join(';', graph.Diagnostics.Select(static row => $"{row.Code}:{row.Message}")));
        Assert.Single(graph.Graph!.RecursionProofs!);
        Assert.Equal(2, graph.Graph.Edges.Count(static row => row.IsDispatchCandidate));
        IrInstruction[] indirectCalls = graph.Methods.Single(static row =>
                row.Identity.MethodName == nameof(DispatchCaller.DispatchWithRecursionRoot)).Import.Program!.Instructions
            .Where(static row => row.Opcode == HybridCpuOpcode.JALR &&
                row.Annotation.ControlFlowKind == IrControlFlowKind.Call &&
                row.Annotation.BranchTargetSymbolName is null).ToArray();
        Assert.NotEmpty(indirectCalls);
        Assert.All(indirectCalls, static indirect => Assert.Equal(0, indirect.Immediate));
    }

    [Fact]
    public void DispatchMetadataAndResolutionHaveNoIseOrLifetimeAuthority()
    {
        Subject subject = CreateSubject();
        Assert.False(subject.Table.HasExecutionAuthority);
        Assert.False(subject.Runtime.HasExecutionAuthority);
        Assert.False(subject.Runtime.HasObjectLifetimeAuthority);
        Assert.Equal(64, subject.Runtime.ContractDigest.Length);
        Assert.DoesNotContain(typeof(HybridCpuManagedDispatchRuntimeV1).Assembly.GetReferencedAssemblies(),
            static row => row.Name is "HybridCPU.Compiler.Core" or "HybridCPU_ISE");
    }

    [Fact]
    public void ManagedAbiV16_DeclaresExactDispatchHelpersAndDefaultOffWorkstream()
    {
        HybridCpuManagedAbiFamilyV1 abi = HybridCpuManagedAbiFamilyV1.Default;
        Assert.Equal((1, 63), (HybridCpuManagedAbiFamilyV1.SchemaMajor, HybridCpuManagedAbiFamilyV1.SchemaMinor));
        Assert.Equal("hybridcpu.runtime-pack/managed-contract-v1.25", HybridCpuManagedAbiFamilyV1.RuntimePackRevision);
        foreach (string helper in new[] { "__hybridcpu_managed_resolve_virtual", "__hybridcpu_managed_resolve_interface",
                     "__hybridcpu_managed_castclass", "__hybridcpu_managed_isinst" })
            Assert.Equal(HybridCpuManagedAbiSupportV1.Supported, abi.ResolveRuntimeHelper(helper)!.Support);
        Assert.Equal(HybridCpuManagedAbiSupportV1.Supported,
            abi.Features.Single(static row => row.Identity == "managed.virtual-interface-dispatch").Support);
        Assert.Equal(HybridCpuManagedWorkstreamSupportV1.QualifiedDefaultOff,
            HybridCpuManagedFeatureSetV1.Default.Workstreams.Single(static row =>
                row.Identity == "virtual-interface-dispatch").Support);
        Assert.False(HybridCpuManagedFeatureSetV1.Default.PublicationAuthority);
    }

    [Fact]
    public void LinkedDispatchMetadata_BootstrapRuntimeAndIseUseOrdinaryAbsolute64AndJalr()
    {
        const string entrySymbol = "phase06_dispatch_entry";
        const string baseSymbol = "phase06_base_target";
        const string derivedSymbol = "phase06_derived_target";
        const string otherSymbol = "phase06_other_target";
        const string resolverSymbol = "__hybridcpu_managed_resolve_virtual";
        Subject seed = CreateSubject();
        Dictionary<ulong, string> symbols = seed.Table.Methods.Where(static row => row.CodeAddress != 0)
            .ToDictionary(static row => row.MethodId, row => row.StableIdentity switch
            {
                BaseMethod => baseSymbol,
                DerivedMethod => derivedSymbol,
                OtherMethod => otherSymbol,
                _ => throw new InvalidOperationException(row.StableIdentity)
            });
        HybridCpuManagedDispatchMetadataEmitterV1 emitter = new HybridCpuManagedDispatchMetadataEmitterV1();
        HybridCpuManagedDispatchMetadataArtifactV1 metadata = emitter.Emit(
            seed.Table.Methods.Where(static row => row.CodeAddress != 0).Select(row =>
                new HybridCpuManagedMethodSymbolBindingV1(row.MethodId, row.StableIdentity, symbols[row.MethodId])),
            seed.Table.VirtualEntries, seed.Table.InterfaceEntries);
        HybridCpuManagedDispatchMetadataArtifactV1 repeated = emitter.Emit(
            seed.Table.Methods.Where(static row => row.CodeAddress != 0).Reverse().Select(row =>
                new HybridCpuManagedMethodSymbolBindingV1(row.MethodId, row.StableIdentity, symbols[row.MethodId])),
            seed.Table.VirtualEntries.Reverse(), seed.Table.InterfaceEntries.Reverse());
        Assert.True(metadata.IsSuccess, metadata.Reason);
        Assert.Equal(metadata.Digest, repeated.Digest);
        Assert.Equal(metadata.Section!.Data, repeated.Section!.Data);
        Assert.Equal(metadata.Relocations, repeated.Relocations);
        Assert.False(metadata.HasLinkAuthority);
        Assert.False(metadata.HasIseExecutionAuthority);

        HybridCpuBundleSerializer serializer = new HybridCpuBundleSerializer();
        byte[] entryCode = serializer.SerializeProgram([
            Bundle(Addi(18, 10, 0)),
            Bundle(Call()),
            Bundle(Addi(5, 10, 0)),
            Bundle(Addi(10, 18, 0)),
            Bundle(IndirectCall())]);
        byte[] baseCode = serializer.SerializeProgram([Bundle(Addi(10, 10, 1)), Bundle(Return())]);
        byte[] derivedCode = serializer.SerializeProgram([Bundle(Addi(10, 10, 2)), Bundle(Return())]);
        byte[] otherCode = serializer.SerializeProgram([Bundle(Addi(10, 10, 3)), Bundle(Return())]);
        byte[] resolverCode = serializer.SerializeProgram([Bundle(Addi(10, 20, 0)), Bundle(Return())]);
        HybridCpuObjectWriterV1 writer = new HybridCpuObjectWriterV1();
        byte[] CodeObject(string symbol, byte[] code, IReadOnlyList<HybridCpuObjectSymbolV1>? declarations = null,
            IReadOnlyList<HybridCpuObjectRelocationV1>? relocations = null)
        {
            HybridCpuObjectSymbolV1 definition = new(symbol, HybridCpuSymbolBinding.Global,
                HybridCpuSymbolVisibility.Default, ".text", 0, (ulong)code.Length, true);
            HybridCpuObjectArtifactV1 artifact = writer.Write(new(
                [new(".text", HybridCpuObjectSectionKind.Code, HybridCpuBundleSerializer.BundleSizeBytes, code, (ulong)code.Length)],
                [definition, .. declarations ?? []], relocations ?? [],
                HybridCpuTargetPlatformContractV1.Default.ContractDigest, HybridCpuManagedAbiFamilyV1.Default.ContractDigest));
            Assert.Equal(HybridCpuObjectStatusV1.Success, artifact.Status);
            return artifact.Bytes;
        }
        HybridCpuObjectArtifactV1 metadataObject = writer.Write(new([metadata.Section],
            symbols.Values.Order(StringComparer.Ordinal).Select(static symbol => new HybridCpuObjectSymbolV1(
                symbol, HybridCpuSymbolBinding.Global, HybridCpuSymbolVisibility.Default, null, 0, 0, false)).ToArray(),
            metadata.Relocations, HybridCpuTargetPlatformContractV1.Default.ContractDigest,
            HybridCpuManagedAbiFamilyV1.Default.ContractDigest));
        Assert.Equal(HybridCpuObjectStatusV1.Success, metadataObject.Status);
        HybridCpuObjectSymbolV1 resolverDeclaration = new HybridCpuObjectSymbolV1(resolverSymbol, HybridCpuSymbolBinding.Global,
            HybridCpuSymbolVisibility.Default, null, 0, 0, false);
        HybridCpuStaticLinkArtifactV1 link = new HybridCpuStaticLinkerV1().Link([
            new("a-entry", CodeObject(entrySymbol, entryCode, [resolverDeclaration],
                [new(".text", (ulong)HybridCpuBundleSerializer.BundleSizeBytes,
                    HybridCpuRelocationKind.ManagedCallRelativeSigned16, resolverSymbol, 0)])),
            new("b-base", CodeObject(baseSymbol, baseCode)),
            new("c-derived", CodeObject(derivedSymbol, derivedCode)),
            new("d-other", CodeObject(otherSymbol, otherCode)),
            new("e-resolver", CodeObject(resolverSymbol, resolverCode)),
            new("z-dispatch", metadataObject.Bytes)]);
        Assert.Equal(HybridCpuLinkStatusV1.Success, link.Status);
        HybridCpuAppliedRelocationV1[] metadataRelocations = link.AppliedRelocations
            .Where(static row => row.Kind == HybridCpuRelocationKind.Absolute64).ToArray();
        Assert.Equal(metadata.Relocations.Count, metadataRelocations.Length);
        Assert.Single(link.AppliedRelocations, static row =>
            row.Kind == HybridCpuRelocationKind.ManagedCallRelativeSigned16 && row.TargetSymbol == resolverSymbol);
        HybridCpuLinkedSectionV1 linkedMetadata = link.Sections.Single(static row =>
            row.ModuleIdentity == "z-dispatch" && row.Name == HybridCpuManagedDispatchMetadataEmitterV1.SectionName);
        int metadataOffset = checked((int)(linkedMetadata.Address - link.ImageBase));
        byte[] linkedMetadataBytes = link.ImageBytes.AsSpan(metadataOffset, checked((int)linkedMetadata.Size)).ToArray();
        Assert.Equal("HCDP0001"u8.ToArray(), linkedMetadataBytes.AsSpan(0, 8).ToArray());
        foreach (HybridCpuAppliedRelocationV1 relocation in metadataRelocations)
        {
            int localOffset = checked((int)(relocation.PlaceAddress - linkedMetadata.Address));
            Assert.Equal(relocation.TargetAddress,
                BinaryPrimitives.ReadUInt64LittleEndian(linkedMetadataBytes.AsSpan(localOffset, 8)));
        }

        ulong Address(string name) => link.Symbols.Single(row => row.Name == name).Address;
        Subject subject = CreateSubject(Address(baseSymbol), Address(derivedSymbol), Address(otherSymbol));
        ulong receiver = Allocate(subject, typeof(DispatchDerived).FullName!);
        HybridCpuManagedDispatchResultV1 resolved = subject.Runtime.ResolveVirtual(receiver, subject.VirtualSlot);
        Assert.Equal(Address(derivedSymbol), resolved.CodeAddress);
        Assert.Contains(link.AppliedRelocations, row => row.TargetSymbol == derivedSymbol && row.TargetAddress == resolved.CodeAddress);

        HybridCpuRuntimeHelperV1 resolverAbi = HybridCpuManagedAbiFamilyV1.Default.ResolveRuntimeHelper(resolverSymbol)!;
        HybridCpuImageRuntimeBootstrapDescriptorV1 descriptor = HybridCpuImageRuntimeBootstrapContractV1.Create(
            HybridCpuManagedAbiFamilyV1.Default.ContractDigest, entrySymbol, entrySymbol,
            [new(resolverSymbol, resolverAbi.Signature, true)], managedTypes:
            subject.Types.Descriptors.Select(static row => new HybridCpuManagedTypeRegistrationV1(
                row.TypeId, row.StableIdentity, row.DescriptorDigest, 0, 1, null)).ToArray());
        HybridCpuRestrictedImageBuilderV1 imageBuilder = new HybridCpuRestrictedImageBuilderV1();
        HybridCpuRestrictedImageV1 image = imageBuilder.Inspect(imageBuilder.Build(
            new(link, entrySymbol, RuntimeBootstrap: descriptor)).PackageBytes);
        Assert.Equal(HybridCpuStartupStatusV1.Success, image.Status);
        DeterministicRuntimeKernelV1 kernel = new DeterministicRuntimeKernelV1();
        Assert.True(kernel.Boot(new(HybridCpuPlatformContractV1.ContractDigest, image.OptionsDigest,
            image.ImageBase, AlignUp((ulong)image.ImageBytes.Length, 4096), image.EntryAddress,
            HybridCpuRestrictedStartupOptionsV1.Production.StackBase,
            HybridCpuRestrictedStartupOptionsV1.Production.StackSize, 0)).IsSuccess);
        Assert.True(new HybridCpuManagedBootstrapRuntimeV1(HybridCpuManagedAbiFamilyV1.Default.ContractDigest)
            .Bootstrap(descriptor, kernel, new Dictionary<string, HybridCpuRuntimeHelperEntryV1>
            {
                [resolverSymbol] = static context => context.ContextCarrierAddress != 0
            }, subject.Types).IsSuccess);

        Processor.MultiBankMemoryArea memory = new Processor.MultiBankMemoryArea(4, 0x400000UL);
        Processor.CPU_Core core = new Processor.CPU_Core(0, CpuCorePlatformContext.CreateFixed(memory, ProcessorMode.Compiler));
        core.InitializePipeline();
        core.PrepareExecutionStart(image.EntryAddress);
        core.WriteCommittedArch(0, 10, receiver);
        core.WriteCommittedArch(0, 20, resolved.CodeAddress);
        CompilerRefPlan7Phase03ManagedHeapAllocatorTests.RetireBundle(core, ReadBundle(image, image.EntryAddress), image.EntryAddress);
        Assert.Equal(receiver, core.ReadArch(0, 18));
        ulong entryCall = image.EntryAddress + (ulong)HybridCpuBundleSerializer.BundleSizeBytes;
        CompilerRefPlan7Phase03ManagedHeapAllocatorTests.RetireBundle(core, ReadBundle(image, entryCall), entryCall);
        Assert.Equal(Address(resolverSymbol), core.ReadCommittedPc(0));
        CompilerRefPlan7Phase03ManagedHeapAllocatorTests.RetireBundle(core,
            ReadBundle(image, Address(resolverSymbol)), Address(resolverSymbol));
        ulong resolverReturn = Address(resolverSymbol) + (ulong)HybridCpuBundleSerializer.BundleSizeBytes;
        CompilerRefPlan7Phase03ManagedHeapAllocatorTests.RetireBundle(core,
            ReadBundle(image, resolverReturn), resolverReturn);
        ulong copyTarget = entryCall + (ulong)HybridCpuBundleSerializer.BundleSizeBytes;
        Assert.Equal(copyTarget, core.ReadCommittedPc(0));
        CompilerRefPlan7Phase03ManagedHeapAllocatorTests.RetireBundle(core, ReadBundle(image, copyTarget), copyTarget);
        ulong restoreReceiver = copyTarget + (ulong)HybridCpuBundleSerializer.BundleSizeBytes;
        CompilerRefPlan7Phase03ManagedHeapAllocatorTests.RetireBundle(core,
            ReadBundle(image, restoreReceiver), restoreReceiver);
        ulong indirectCall = restoreReceiver + (ulong)HybridCpuBundleSerializer.BundleSizeBytes;
        CompilerRefPlan7Phase03ManagedHeapAllocatorTests.RetireBundle(core,
            ReadBundle(image, indirectCall), indirectCall);
        Assert.Equal(resolved.CodeAddress, core.ReadCommittedPc(0));
        CompilerRefPlan7Phase03ManagedHeapAllocatorTests.RetireBundle(core, ReadBundle(image, resolved.CodeAddress), resolved.CodeAddress);
        Assert.Equal(receiver + 2, core.ReadArch(0, 10));
    }

    private static RestrictedCilImportResultV1 Import(string method, Subject subject, bool? virtualDispatch)
    {
        List<RestrictedCilDispatchBindingV1> dispatch = new List<RestrictedCilDispatchBindingV1>();
        if (virtualDispatch is bool isVirtual)
        {
            int token = isVirtual ? typeof(DispatchBase).GetMethod(nameof(DispatchBase.Invoke))!.MetadataToken
                : typeof(IDispatchCorpus).GetMethod(nameof(IDispatchCorpus.Invoke))!.MetadataToken;
            dispatch.Add(new(token, isVirtual ? BaseMethod : InterfaceMethod,
                [RestrictedCilTypeV1.ObjectReference, RestrictedCilTypeV1.Int32], RestrictedCilTypeV1.Int32,
                isVirtual ? RestrictedCilDispatchKindV1.Virtual : RestrictedCilDispatchKindV1.Interface,
                isVirtual ? subject.VirtualSlot : subject.InterfaceSlot,
                isVirtual ? null : subject.InterfaceTypeId));
        }
        HybridCpuManagedTypeDescriptorV1 derived = Type(subject, typeof(DispatchDerived).FullName!);
        RestrictedCilImporterV1 importer = new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2,
            dispatchBindings: dispatch,
            typeTestBindings: [new(typeof(DispatchDerived).MetadataToken, derived, subject.Types.TypeHandle(derived.TypeId)!.Value)]);
        return importer.ImportImage(FixtureImage, new(typeof(DispatchCaller).FullName!, method), $"phase06-{method}");
    }

    private static Subject CreateSubject(ulong baseAddress = 0x1000, ulong derivedAddress = 0x2000,
        ulong otherAddress = 0x3000)
    {
        string iface = typeof(IDispatchCorpus).FullName!;
        string baseType = typeof(DispatchBase).FullName!;
        string derivedType = typeof(DispatchDerived).FullName!;
        string otherType = typeof(DispatchOther).FullName!;
        HybridCpuManagedTypeSystemBuildV1 typeBuild = new HybridCpuManagedTypeSystemBuilderV1().Build([
            new(iface, HybridCpuManagedTypeKindV1.Interface, null, [], []),
            new(baseType, HybridCpuManagedTypeKindV1.Class, null, [iface], []),
            new(derivedType, HybridCpuManagedTypeKindV1.Class, baseType, [], []),
            new(otherType, HybridCpuManagedTypeKindV1.Class, null, [iface], [])
        ]);
        Assert.True(typeBuild.IsSuccess, typeBuild.Reason);
        HybridCpuManagedTypeSystemV1 types = typeBuild.TypeSystem!;
        ulong interfaceTypeId = types.Descriptors.Single(row => row.StableIdentity == iface).TypeId;
        ulong baseTypeId = types.Descriptors.Single(row => row.StableIdentity == baseType).TypeId;
        ulong derivedTypeId = types.Descriptors.Single(row => row.StableIdentity == derivedType).TypeId;
        ulong otherTypeId = types.Descriptors.Single(row => row.StableIdentity == otherType).TypeId;
        ulong virtualSlot = HybridCpuManagedDispatchTableBuilderV1.ComputeSlotId(baseTypeId, BaseMethod, Signature);
        ulong interfaceSlot = HybridCpuManagedDispatchTableBuilderV1.ComputeSlotId(interfaceTypeId, InterfaceMethod, Signature);
        HybridCpuManagedDispatchTableBuildV1 tableBuild = new HybridCpuManagedDispatchTableBuilderV1().Build(types,
        [
            new(InterfaceMethod, interfaceTypeId, Signature, 0, 0, true, true),
            new(BaseMethod, baseTypeId, Signature, baseAddress, 0, true, true),
            new(DerivedMethod, derivedTypeId, Signature, derivedAddress, 0, true, false, virtualSlot),
            new(OtherMethod, otherTypeId, Signature, otherAddress, 0, false, false)
        ],
        [
            new(baseTypeId, interfaceTypeId, interfaceSlot, BaseMethod),
            new(otherTypeId, interfaceTypeId, interfaceSlot, OtherMethod)
        ]);
        Assert.True(tableBuild.IsSuccess, tableBuild.Reason);
        DeterministicRuntimeKernelV1 kernel = new DeterministicRuntimeKernelV1();
        Assert.True(kernel.Boot(new(HybridCpuPlatformContractV1.ContractDigest, new string('a', 64),
            0x100000, 4096, 0x100000, 0x200000, 4096, 0)).IsSuccess);
        HybridCpuManagedHeapAllocatorV1 heap = new HybridCpuManagedHeapAllocatorV1(kernel, types,
            HybridCpuManagedHeapOptionsV1.Create(0x40000000, 4096, 4096, -3));
        Assert.True(heap.Initialize().IsSuccess);
        return new(types, heap, tableBuild.Table!, new(types, heap, tableBuild.Table!),
            virtualSlot, interfaceSlot, interfaceTypeId);
    }

    private static ulong Allocate(Subject subject, string typeIdentity)
    {
        HybridCpuManagedTypeDescriptorV1 type = Type(subject, typeIdentity);
        HybridCpuManagedHeapResultV1 result = subject.Heap.Allocate(subject.Types.TypeHandle(type.TypeId)!.Value);
        Assert.True(result.IsSuccess, result.Reason);
        return result.ObjectReference;
    }

    private static HybridCpuManagedTypeDescriptorV1 Type(Subject subject, string identity) =>
        subject.Types.Descriptors.Single(row => row.StableIdentity == identity);

    private static IrRegisterAllocationResultV1 Allocate(IrProgram program)
    {
        IrProgramSchedule schedule = new HybridCpuLocalListScheduler().ScheduleProgram(program);
        IrProgramBundlingResult bundles = new HybridCpuBundleFormer().BundleProgram(schedule);
        return new HybridCpuScheduleAwareRegisterAllocatorV1().Allocate(schedule, bundles,
            resourceModel: ResourceModel, options: HybridCpuRegisterAllocationOptionsV1.Qualification);
    }

    private static HybridCpuInstructionBundle Bundle(params HybridCpuInstructionWord[] instructions)
    {
        HybridCpuInstructionBundle bundle = new HybridCpuInstructionBundle();
        for (int index = 0; index < instructions.Length; index++) bundle.SetInstruction(index, instructions[index]);
        return bundle;
    }

    private static HybridCpuInstructionWord Addi(byte destination, byte source, ushort immediate) => new()
    {
        OpCode = (uint)HybridCpuOpcode.ADDI, DataTypeValue = HybridCpuDataType.INT64, PredicateMask = byte.MaxValue,
        Word1 = HybridCpuInstructionWord.PackArchRegs(destination, source, HybridCpuInstructionWord.NoArchReg),
        Immediate = immediate
    };

    private static HybridCpuInstructionWord IndirectCall() => new()
    {
        OpCode = (uint)HybridCpuOpcode.JALR, DataTypeValue = HybridCpuDataType.INT64, PredicateMask = byte.MaxValue,
        Word1 = HybridCpuInstructionWord.PackArchRegs((byte)HybridCpuNativeAbiContractV2.ReturnAddressRegister, 5,
            HybridCpuInstructionWord.NoArchReg)
    };

    private static HybridCpuInstructionWord Call() => new()
    {
        OpCode = (uint)HybridCpuOpcode.JAL, DataTypeValue = HybridCpuDataType.INT64, PredicateMask = byte.MaxValue,
        Word1 = HybridCpuInstructionWord.PackArchRegs((byte)HybridCpuNativeAbiContractV2.ReturnAddressRegister,
            HybridCpuInstructionWord.NoArchReg, HybridCpuInstructionWord.NoArchReg)
    };

    private static HybridCpuInstructionWord Return() => new()
    {
        OpCode = (uint)HybridCpuOpcode.JALR, DataTypeValue = HybridCpuDataType.INT64, PredicateMask = byte.MaxValue,
        Word1 = HybridCpuInstructionWord.PackArchRegs(0, (byte)HybridCpuNativeAbiContractV2.ReturnAddressRegister,
            HybridCpuInstructionWord.NoArchReg), Immediate = HybridCpuNativeCallControlContractV1.ManagedReturnAdjustmentBytes
    };

    private static VLIW_Instruction[] ReadBundle(HybridCpuRestrictedImageV1 image, ulong address) =>
        CompilerRefPlan7Phase03ManagedHeapAllocatorTests.ReadBundle(image, address);

    private static ulong AlignUp(ulong value, ulong alignment) => checked((value + alignment - 1) / alignment * alignment);

    private sealed record Subject(HybridCpuManagedTypeSystemV1 Types, HybridCpuManagedHeapAllocatorV1 Heap,
        HybridCpuManagedDispatchTableV1 Table, HybridCpuManagedDispatchRuntimeV1 Runtime,
        ulong VirtualSlot, ulong InterfaceSlot, ulong InterfaceTypeId);
}
