using HybridCPU_ISE.CloseToHSL.Core.Runtime.Managed;
using HybridCPU.Compiler.Cil;
using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.IR.Resources;
using HybridCPU.Compiler.Core.Target;
using HybridCPU.Compiler.Core.Target.Managed;
using HybridCPU.Compiler.Core.Target.Runtime;
using HybridCPU.ManagedRuntime;
using HybridCPU.Platform.Contracts;
using HybridCPU.RuntimeKernel;
using HybridCPU_ISE.Arch;
using YAKSys_Hybrid_CPU;
using YAKSys_Hybrid_CPU.Memory;

namespace HybridCPU_ISE.Tests.CompilerTests;

public sealed class CompilerRefPlan7Phase02ManagedReferencesTypeLayoutTests
{
    private static readonly byte[] FixtureImage = File.ReadAllBytes(typeof(Phase02ManagedReferenceFixtures).Assembly.Location);
    private static readonly string FixtureType = typeof(Phase02ManagedReferenceFixtures).FullName!;
    private static readonly HybridCpuMiiResourceModelV1 ResourceModel =
        HybridCpuMiiResourceModelV1.Create(new HybridCpuMachineTopologyV1(64, 64, 4, 8, 2, 16), 8);

    [Fact]
    public void RuntimeOwnedLayout_IsGoldenAlignedAndCompilerEncodingIsContractBound()
    {
        HybridCpuManagedTypeSystemV1 system = BuildTypes([Interface(), Base(), Derived()]);
        HybridCpuManagedTypeDescriptorV1 @base = system.Descriptors.Single(static type => type.StableIdentity == "Example.Base");
        HybridCpuManagedTypeDescriptorV1 derived = system.Descriptors.Single(static type => type.StableIdentity == "Example.Derived");

        Assert.Equal(HybridCpuPlatformContractV1.ManagedObjectHeaderSizeBytes, @base.InstanceFields[0].OffsetBytes);
        Assert.Equal(24, @base.InstanceFields[1].OffsetBytes);
        Assert.Equal(32, @base.InstanceSizeBytes);
        Assert.Equal(32, derived.InstanceFields[0].OffsetBytes);
        Assert.Equal(40, derived.InstanceSizeBytes);
        Assert.Equal([0], derived.StaticLayout.ObjectReferenceOffsets);
        Assert.Equal(16, derived.StaticLayout.SizeBytes);

        HybridCpuManagedTypeMetadataArtifactV1 encoded = new HybridCpuManagedTypeMetadataEncoderV1().Encode(system.Descriptors);
        Assert.Equal(HybridCpuManagedTypeMetadataStatusV1.Encoded, encoded.Status);
        Assert.NotEmpty(encoded.Bytes);
        Assert.Equal(64, encoded.Digest.Length);
    }

    [Fact]
    public void TypeIdentityAndAssignability_AreDeterministicAcrossInputOrder()
    {
        HybridCpuManagedTypeSystemV1 first = BuildTypes([Interface(), Base(), Derived()]);
        HybridCpuManagedTypeSystemV1 second = BuildTypes([Derived(), Base(), Interface()]);
        HybridCpuManagedTypeDescriptorV1 derived = first.Descriptors.Single(static type => type.StableIdentity == "Example.Derived");
        HybridCpuManagedTypeDescriptorV1 @base = first.Descriptors.Single(static type => type.StableIdentity == "Example.Base");
        HybridCpuManagedTypeDescriptorV1 contract = first.Descriptors.Single(static type => type.StableIdentity == "Example.IContract");

        Assert.Equal(first.Digest, second.Digest);
        Assert.Equal(first.Descriptors.Select(static type => type.DescriptorDigest),
            second.Descriptors.Select(static type => type.DescriptorDigest));
        Assert.True(first.IsAssignable(derived.TypeId, @base.TypeId));
        Assert.True(first.IsAssignable(derived.TypeId, contract.TypeId));
        Assert.False(first.IsAssignable(@base.TypeId, derived.TypeId));
        Assert.False(first.IsAssignable(0, contract.TypeId));
    }

    [Fact]
    public void MalformedMissingAndCyclicMetadata_FailClosed()
    {
        HybridCpuManagedTypeSystemBuilderV1 builder = new HybridCpuManagedTypeSystemBuilderV1();
        HybridCpuManagedTypeSystemBuildV1 missing = builder.Build([
            new("Example.Missing", HybridCpuManagedTypeKindV1.Class, "Example.Absent", [], [])
        ]);
        HybridCpuManagedTypeSystemBuildV1 cycle = builder.Build([
            new("Example.A", HybridCpuManagedTypeKindV1.Class, "Example.B", [], []),
            new("Example.B", HybridCpuManagedTypeKindV1.Class, "Example.A", [], [])
        ]);
        HybridCpuManagedTypeSystemV1 valid = BuildTypes([Base()]);
        HybridCpuManagedTypeDescriptorV1 descriptor = valid.Descriptors.Single();
        HybridCpuManagedTypeMetadataArtifactV1 tampered = new HybridCpuManagedTypeMetadataEncoderV1().Encode([
            descriptor with { DescriptorDigest = new string('0', 64) }
        ]);

        Assert.Equal(HybridCpuManagedTypeSystemStatusV1.MissingDependency, missing.Status);
        Assert.Equal(HybridCpuManagedTypeSystemStatusV1.CyclicDependency, cycle.Status);
        Assert.Equal(HybridCpuManagedTypeMetadataStatusV1.InvalidInput, tampered.Status);
        Assert.Empty(tampered.Bytes);
    }

    [Fact]
    public void StaticStorageAndTypeInitialization_StateMachineIsMonotonic()
    {
        HybridCpuManagedTypeSystemV1 system = BuildTypes([Base()]);
        ulong typeId = system.Descriptors.Single().TypeId;

        Assert.Equal(HybridCpuManagedTypeInitializationStateV1.Uninitialized, system.InitializationState(typeId));
        Assert.True(system.TryBeginInitialization(typeId));
        Assert.False(system.TryBeginInitialization(typeId));
        Assert.True(system.TryCompleteInitialization(typeId, succeeded: false));
        Assert.Equal(HybridCpuManagedTypeInitializationStateV1.Failed, system.InitializationState(typeId));
        Assert.False(system.TryCompleteInitialization(typeId, succeeded: true));
        Assert.NotNull(system.StaticStorage(typeId));
        Assert.Null(system.StaticStorage(0));
    }

    [Fact]
    public void FieldLowering_ConsumesRuntimeLayoutAndNeverUsesImplicitNullFault()
    {
        HybridCpuManagedTypeSystemV1 system = BuildTypes([Interface(), Base(), Derived()]);
        HybridCpuManagedTypeDescriptorV1 descriptor = system.Descriptors
            .Single(static type => type.StableIdentity == "Example.Base");
        HybridCpuManagedTypeDescriptorV1 derived = system.Descriptors
            .Single(static type => type.StableIdentity == "Example.Derived");
        HybridCpuManagedFieldLoweringV1 lowerer = new HybridCpuManagedFieldLoweringV1();

        HybridCpuManagedFieldLoweringPlanV1 instance = lowerer.Lower(
            descriptor, "child", false, HybridCpuManagedFieldAccessKindV1.Load);
        HybridCpuManagedFieldLoweringPlanV1 missingStaticSymbol = lowerer.Lower(
            derived, "global", true, HybridCpuManagedFieldAccessKindV1.Store);
        HybridCpuManagedFieldLoweringPlanV1 staticStore = lowerer.Lower(
            derived, "global", true, HybridCpuManagedFieldAccessKindV1.Store, "__managed_static_Example_Derived");

        Assert.Equal(HybridCpuManagedFieldLoweringStatusV1.Lowered, instance.Status);
        Assert.Equal(HybridCpuManagedFieldLoweringStepKindV1.ExplicitNullCheckHelperCall, instance.Steps[0].Kind);
        Assert.Equal("__hybridcpu_managed_null_check", instance.Steps[0].Symbol);
        Assert.Equal(24, instance.Steps[1].Immediate);
        Assert.Equal(HybridCpuManagedFieldLoweringStepKindV1.Load, instance.Steps[2].Kind);
        Assert.Equal(HybridCpuManagedFieldLoweringStatusV1.InvalidInput, missingStaticSymbol.Status);
        Assert.Equal(HybridCpuManagedFieldLoweringStatusV1.Lowered, staticStore.Status);
        Assert.Equal(HybridCpuManagedFieldLoweringStepKindV1.MaterializeStaticBase, staticStore.Steps[0].Kind);
        Assert.Equal(HybridCpuManagedFieldLoweringStepKindV1.Store, staticStore.Steps[2].Kind);
    }

    [Fact]
    public void ExplicitNullCheck_UsesRuntimeKernelProcessExitAndDoesNotCreateAnIseFault()
    {
        DeterministicRuntimeKernelV1 kernel = new DeterministicRuntimeKernelV1();
        HybridCpuKernelBootResultV1 boot = kernel.Boot(new(
            HybridCpuPlatformContractV1.ContractDigest, new string('a', 64),
            0x1000, 0x1000, 0x1000, 0x4000, 0x2000, 0));
        Assert.True(boot.IsSuccess, boot.Reason);
        HybridCpuManagedReferenceRuntimeV1 runtime = new HybridCpuManagedReferenceRuntimeV1();

        HybridCpuManagedNullCheckResultV1 valid = runtime.CheckNotNull(0x1234, kernel);
        HybridCpuManagedNullCheckResultV1 failed = runtime.CheckNotNull(0, kernel);

        Assert.True(valid.MayContinue);
        Assert.Equal(0x1234UL, valid.Reference);
        Assert.Equal(HybridCpuManagedNullCheckStatusV1.NullTerminated, failed.Status);
        Assert.False(failed.MayContinue);
        Assert.Equal(HybridCpuKernelStatusV1.NoCurrentContext, kernel.ProcessExit(0).Status);
    }

    [Fact]
    public void TypeDescriptorRegistration_RoundTripsThroughVersionedBootstrapMetadata()
    {
        HybridCpuManagedTypeDescriptorV1 type = BuildTypes([Base()]).Descriptors.Single();
        HybridCpuManagedTypeRegistrationV1 registration = new HybridCpuManagedTypeRegistrationV1(
            type.TypeId, type.StableIdentity, type.DescriptorDigest, 128, 96, null);
        HybridCpuImageRuntimeBootstrapDescriptorV1 descriptor = HybridCpuImageRuntimeBootstrapContractV1.Create(
            HybridCpuManagedAbiFamilyV1.Default.ContractDigest, "runtime", "managed", managedTypes: [registration]);

        byte[] first = HybridCpuManagedBootstrapEncodingV1.Encode(descriptor);
        byte[] second = HybridCpuManagedBootstrapEncodingV1.Encode(descriptor);
        HybridCpuImageRuntimeBootstrapDescriptorV1 decoded = HybridCpuManagedBootstrapEncodingV1.Decode(first);

        Assert.Equal(first, second);
        Assert.Equal(registration, Assert.Single(decoded.ManagedTypes!));
        Assert.Equal(descriptor.DescriptorDigest, decoded.DescriptorDigest);
    }

    [Fact]
    public void ManagedCallAbi_UsesOrdinaryPointerCarrierAndRejectsByrefs()
    {
        HybridCpuManagedAbiFamilyV1 family = HybridCpuManagedAbiFamilyV1.Default;
        HybridCpuManagedCallLayoutV1 objects = family.ClassifyCall(new(
            HybridCpuManagedCallDirectionV1.ManagedToManaged,
            [new("receiver", HybridCpuManagedValueKindV1.ObjectReference, 8, 8)],
            new("result", HybridCpuManagedValueKindV1.ObjectReference, 8, 8)));
        HybridCpuManagedCallLayoutV1 byref = family.ClassifyCall(new(
            HybridCpuManagedCallDirectionV1.ManagedToManaged,
            [new("address", HybridCpuManagedValueKindV1.ManagedByRef, 8, 8)], null));
        HybridCpuAbiLayoutV2 nativePointer = HybridCpuNativeAbiContractV2.Default.Classify(new(
            [new("receiver", HybridCpuAbiValueKindV2.Pointer, 8, 8)],
            new("result", HybridCpuAbiValueKindV2.Pointer, 8, 8)));

        Assert.Equal(HybridCpuPlatformFactStatus.Supported, objects.Status);
        Assert.Equal(nativePointer.Digest, objects.NativeLayout!.Digest);
        Assert.Equal(HybridCpuPlatformFactStatus.Unsupported, byref.Status);
        Assert.False(family.HasRuntimeAuthority);
        Assert.False(family.HasGcPhaseAuthority);
    }

    [Fact]
    public void CilObjectReferences_SurvivePhiDirectCallSchedulingAndAllocation()
    {
        ManagedCallGraphCompilationV1 graph = ImportWorld(nameof(Phase02ManagedReferenceFixtures.ReferenceRoot));

        Assert.Equal(RestrictedCilImportStatusV1.Success, graph.Status);
        Assert.Equal(2, graph.Methods.Count);
        ManagedCompiledMethodV1 root = graph.Methods.Single(static method => method.Identity.MethodName == nameof(Phase02ManagedReferenceFixtures.ReferenceRoot));
        IrProgram program = root.Import.Program!;
        Assert.Contains(root.Import.ControlFlowAnalysis!.Phis, static phi => phi.Type == RestrictedCilTypeV1.ObjectReference);
        Assert.Contains(program.ValueFlow.Values, static value =>
            value.ValueKind.Kind == IrCanonicalValueKind.ManagedObjectReference &&
            value.VirtualClass == IrVirtualValueClass.ManagedObjectReference &&
            value.Allocation.LegalContours.Contains("managed-object-reference"));
        Assert.Contains(program.Instructions, static instruction => instruction.Annotation.ControlFlowKind == IrControlFlowKind.Call);

        IrProgramSchedule schedule = new HybridCpuLocalListScheduler().ScheduleProgram(program);
        IrProgramBundlingResult bundles = new HybridCpuBundleFormer().BundleProgram(schedule);
        IrRegisterAllocationResultV1 allocation = new HybridCpuScheduleAwareRegisterAllocatorV1().Allocate(
            schedule, bundles, resourceModel: ResourceModel, options: HybridCpuRegisterAllocationOptionsV1.Qualification);
        Assert.Equal(IrRegisterAllocationStatusV1.Allocated, allocation.Status);
        Assert.Contains(allocation.Witness!.SemanticValues, static value =>
            value.ValueKind.Kind == IrCanonicalValueKind.ManagedObjectReference &&
            value.VirtualClass == IrVirtualValueClass.ManagedObjectReference);
    }

    [Fact]
    public void ObjectReferencePhiAndDirectCall_ExecuteThroughLinkedImageAndIseAsOrdinaryValue()
    {
        ManagedCallGraphCompilationV1 graph = ImportWorld(nameof(Phase02ManagedReferenceFixtures.ReferenceRoot));
        string entry = graph.Methods.Single(static method =>
            method.Identity.MethodName == nameof(Phase02ManagedReferenceFixtures.ReferenceRoot)).Identity.StableIdentity;
        ScalarControlFlowV2LinkedProgramV1 linked = new ScalarControlFlowV2ObjectLinkerV1().Link(graph, entry);
        Assert.Equal(ScalarControlFlowV2LinkStatusV1.Success, linked.Status);
        HybridCpuRestrictedImageV1 image = Assert.IsType<HybridCpuRestrictedImageV1>(linked.RestrictedImage);
        HybridCpuStartupRegisterStateV1 registers = Assert.IsType<HybridCpuStartupRegisterStateV1>(image.InitialRegisters);
        const ulong objectReference = 0x0000_0000_2000_0080UL;

        Processor.MainMemoryArea originalMemory = Processor.MainMemory;
        ProcessorMode originalMode = Processor.CurrentProcessorMode;
        MemorySubsystem? originalMemorySubsystem = Processor.Memory;
        try
        {
            Processor.CurrentProcessorMode = ProcessorMode.Compiler;
            Processor.Memory = null;
            Processor.MainMemory = new HybridCpuIseSparseMainMemoryAreaV1();
            Processor.CPU_Core core = new Processor.CPU_Core(0,
                CpuCorePlatformContext.CreateFixed(Processor.MainMemory, ProcessorMode.Compiler));
            core.InitializePipeline();
            core.PrepareExecutionStart(image.EntryAddress);
            for (int register = 0; register < 32; register++) core.WriteCommittedArch(0, register, 0);
            core.WriteCommittedPc(0, image.EntryAddress);
            core.WriteCommittedArch(0, registers.StackPointerRegister, registers.StackPointer);
            core.WriteCommittedArch(0, registers.ReturnAddressRegister, registers.ReturnAddress);
            core.WriteCommittedArch(0, HybridCpuNativeAbiContractV2.Default.ArgumentRegisters[0], objectReference);
            core.WriteCommittedArch(0, HybridCpuNativeAbiContractV2.Default.ArgumentRegisters[1], 5);

            int retired = 0;
            while (core.ReadCommittedPc(0) != HybridCpuRestrictedStartupOptionsV1.Production.ReturnSentinel && retired++ < 256)
            {
                ulong pc = core.ReadCommittedPc(0);
                int bundleIndex = checked((int)((pc - image.ImageBase) / HybridCpuBundleSerializer.BundleSizeBytes));
                Assert.InRange(bundleIndex, 0, image.ImageBytes.Length / HybridCpuBundleSerializer.BundleSizeBytes - 1);
                VLIW_Instruction[] bundle = ReadBundle(image.ImageBytes, bundleIndex);
                bool control = bundle.Any(static instruction => instruction.OpCode is
                    >= (uint)Processor.CPU_Core.InstructionsEnum.JAL and <= (uint)Processor.CPU_Core.InstructionsEnum.BGEU);
                core.TestRunDecodeStageWithFetchedBundle(bundle, pc);
                core.TestRunExecuteStageFromCurrentDecodeState();
                core.TestRunMemoryAndWriteBackStagesFromCurrentExecuteState();
                if (!control || core.ReadCommittedPc(0) == pc)
                    core.WriteCommittedPc(0, checked(pc + (ulong)HybridCpuBundleSerializer.BundleSizeBytes));
            }

            Assert.True(retired <= 256);
            Assert.Equal(HybridCpuRestrictedStartupOptionsV1.Production.ReturnSentinel, core.ReadCommittedPc(0));
            Assert.Equal(objectReference, core.ReadArch(0, registers.ReturnValueRegister));
        }
        finally
        {
            Processor.MainMemory = originalMemory;
            Processor.CurrentProcessorMode = originalMode;
            Processor.Memory = originalMemorySubsystem;
        }
    }

    [Fact]
    public void ManagedByRefSignature_IsRejectedWithStableDiagnostic()
    {
        RestrictedCilImportResultV1 result = new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2)
            .ImportImage(FixtureImage, new(FixtureType, nameof(Phase02ManagedReferenceFixtures.ByRefIdentity)),
                "refplan7-phase02-managed-reference-fixture.dll");

        Assert.Equal(RestrictedCilImportStatusV1.Unsupported, result.Status);
        Assert.Equal("HCCIL1010", Assert.Single(result.Diagnostics).Code);
        Assert.Null(result.Program);
    }

    [Fact]
    public void CilInstanceFieldLoadStore_UseRuntimeBindingAndExplicitNullHelper()
    {
        HybridCpuManagedTypeSystemV1 system = BuildTypes([
            new(typeof(Phase02FieldHolder).FullName!, HybridCpuManagedTypeKindV1.Class, null, [],
            [
                new(nameof(Phase02FieldHolder.Child), HybridCpuManagedStorageKindV1.ObjectReference, 8, 8, false, 0),
                new(nameof(Phase02FieldHolder.Number), HybridCpuManagedStorageKindV1.Primitive, 4, 4, false, 1)
            ])
        ]);
        HybridCpuManagedTypeDescriptorV1 descriptor = system.Descriptors.Single();
        RestrictedCilFieldLayoutBindingV1[] bindings =
        [
            new(typeof(Phase02FieldHolder).FullName!, nameof(Phase02FieldHolder.Child), false,
                RestrictedCilTypeV1.ObjectReference, descriptor),
            new(typeof(Phase02FieldHolder).FullName!, nameof(Phase02FieldHolder.Number), false,
                RestrictedCilTypeV1.Int32, descriptor)
        ];
        RestrictedCilImporterV1 importer = new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2,
            fieldLayouts: bindings);

        RestrictedCilImportResultV1 load = importer.ImportImage(FixtureImage,
            new(FixtureType, nameof(Phase02ManagedReferenceFixtures.LoadChild)), "phase02-fields.dll");
        RestrictedCilImportResultV1 store = importer.ImportImage(FixtureImage,
            new(FixtureType, nameof(Phase02ManagedReferenceFixtures.StoreNumber)), "phase02-fields.dll");
        RestrictedCilImportResultV1 unbound = new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2)
            .ImportImage(FixtureImage, new(FixtureType, nameof(Phase02ManagedReferenceFixtures.LoadChild)), "phase02-fields.dll");

        Assert.Equal(RestrictedCilImportStatusV1.Success, load.Status);
        Assert.Equal(RestrictedCilImportStatusV1.Success, store.Status);
        Assert.Contains(load.Program!.Instructions, static instruction =>
            instruction.Annotation.BranchTargetSymbolName == "__hybridcpu_managed_null_check");
        Assert.Contains(load.Program.Instructions, static instruction =>
            instruction.Opcode == HybridCpuOpcode.LD && instruction.SideEffects.Memory.Kind == IrMemoryEffectKind.Read);
        Assert.Contains(store.Program!.Instructions, static instruction =>
            instruction.Opcode == HybridCpuOpcode.SW && instruction.SideEffects.Memory.Kind == IrMemoryEffectKind.Write);
        Assert.Equal(RestrictedCilImportStatusV1.Unsupported, unbound.Status);
        Assert.Equal("HCCIL1207", Assert.Single(unbound.Diagnostics).Code);
    }

    private static ManagedCallGraphCompilationV1 ImportWorld(string root) =>
        new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2).ImportBodyWorld(new(
            ManagedBodyWorldModeV1.StandaloneRestrictedModule, FixtureImage,
            "refplan7-phase02-managed-reference-fixture.dll", [new(FixtureType, root)], []));

    private static HybridCpuManagedTypeSystemV1 BuildTypes(IEnumerable<HybridCpuManagedTypeDeclarationV1> declarations)
    {
        HybridCpuManagedTypeSystemBuildV1 result = new HybridCpuManagedTypeSystemBuilderV1().Build(declarations);
        Assert.True(result.IsSuccess, result.Reason);
        return result.TypeSystem!;
    }

    private static HybridCpuManagedTypeDeclarationV1 Interface() =>
        new("Example.IContract", HybridCpuManagedTypeKindV1.Interface, null, [], []);

    private static HybridCpuManagedTypeDeclarationV1 Base() => new(
        "Example.Base", HybridCpuManagedTypeKindV1.Class, null, [],
        [
            new("number", HybridCpuManagedStorageKindV1.Primitive, 4, 4, false, 0),
            new("child", HybridCpuManagedStorageKindV1.ObjectReference, 8, 8, false, 1)
        ]);

    private static HybridCpuManagedTypeDeclarationV1 Derived() => new(
        "Example.Derived", HybridCpuManagedTypeKindV1.Class, "Example.Base", ["Example.IContract"],
        [
            new("tag", HybridCpuManagedStorageKindV1.Primitive, 4, 4, false, 0),
            new("global", HybridCpuManagedStorageKindV1.ObjectReference, 8, 8, true, 1),
            new("counter", HybridCpuManagedStorageKindV1.Primitive, 4, 4, true, 2)
        ]);

    private static VLIW_Instruction[] ReadBundle(byte[] image, int bundleIndex)
    {
        VLIW_Bundle bundle = new VLIW_Bundle();
        Assert.True(bundle.TryReadBytes(image, checked(bundleIndex * HybridCpuBundleSerializer.BundleSizeBytes)));
        return Enumerable.Range(0, HybridCpuInstructionBundle.SlotCount).Select(bundle.GetInstruction).ToArray();
    }

}

public static class Phase02ManagedReferenceFixtures
{
    public static object ReferenceIdentity(object value) => value;

    public static object ReferenceRoot(object value, int count)
    {
        object current = count != 0 ? value : null!;
        return ReferenceIdentity(current);
    }

    public static ref object ByRefIdentity(ref object value) => ref value;

    public static object LoadChild(Phase02FieldHolder holder) => holder.Child!;

    public static void StoreNumber(Phase02FieldHolder holder, int value) => holder.Number = value;
}

public sealed class Phase02FieldHolder
{
    public object? Child;
    public int Number;
}
