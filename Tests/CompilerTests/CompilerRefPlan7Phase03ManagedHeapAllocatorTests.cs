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
using HybridCPU.RuntimeKernel;
using HybridCPU_ISE.Arch;
using HybridCPU_ISE.CloseToHSL.Core.Runtime.Managed;
using YAKSys_Hybrid_CPU;
using YAKSys_Hybrid_CPU.Memory;

namespace HybridCPU_ISE.Tests.CompilerTests;

public sealed class CompilerRefPlan7Phase03ManagedHeapAllocatorTests
{
    private static readonly HybridCpuMiiResourceModelV1 ResourceModel =
        HybridCpuMiiResourceModelV1.Create(new HybridCpuMachineTopologyV1(64, 64, 4, 8, 2, 16), 8);

    private static byte[] ReadPhysical(Processor.MainMemoryArea memory, ulong address, int length)
    {
        byte[] bytes = new byte[length];
        Assert.True(memory.TryReadPhysicalRange(address, bytes));
        return bytes;
    }

    [Fact]
    public void IseHeapBacking_WritesIntoExactBoundMainMemoryAndRejectsWrongRange()
    {
        const ulong heapBase = 0x1000;
        const ulong heapSize = 0x4000;
        Processor.MainMemoryArea memory = new Processor.MainMemoryArea();
        memory.SetLength(0x10000);
        HybridCpuIseHeapMemoryV1 backing = new HybridCpuIseHeapMemoryV1(memory, heapBase, heapSize);
        HybridCpuManagedTypeSystemV1 types = Types(Field("value", 4, 4));
        ulong typeHandle = Assert.IsType<ulong>(types.TypeHandle(Assert.Single(types.Descriptors).TypeId));
        DeterministicRuntimeKernelV1 kernel = new DeterministicRuntimeKernelV1();
        Assert.True(kernel.Boot(new(HybridCpuPlatformContractV1.ContractDigest, new string('a', 64),
            0x8000, 4096, 0x8000, 0xc000, 4096, 0, 1000)).IsSuccess);
        HybridCpuManagedHeapOptionsV1 options = HybridCpuManagedHeapOptionsV1.Create(heapBase, heapSize, 4096, -3);
        HybridCpuManagedHeapAllocatorV1 allocator = new HybridCpuManagedHeapAllocatorV1(kernel, types, options, backing);

        Assert.True(allocator.Initialize().IsSuccess);
        ulong reference = allocator.Allocate(typeHandle).ObjectReference;
        Span<byte> header = stackalloc byte[HybridCpuPlatformContractV1.ManagedObjectHeaderSizeBytes];
        Assert.True(memory.TryReadPhysicalRange(reference, header));
        Assert.Equal(typeHandle, BinaryPrimitives.ReadUInt64LittleEndian(header));
        Assert.All(header[8..].ToArray(), static value => Assert.Equal(0, value));
        Assert.False(backing.Write(heapBase - 1, [1]));
        Assert.False(backing.Read(heapBase + heapSize, new byte[1]));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new HybridCpuIseHeapMemoryV1(memory, 0xf000, 0x2000));
    }

    [Fact]
    public void IseManagedImageLoader_MaterializesBootstrapsAndRejectsOverlappingHeap()
    {
        const ulong imageBase = 0x0010_0000;
        const ulong heapBase = 0x2800_0000;
        HybridCpuManagedTypeSystemV1 sourceTypes = Types();
        HybridCpuManagedTypeDescriptorV1 type = Assert.Single(sourceTypes.Descriptors);
        ulong typeHandle = Assert.IsType<ulong>(sourceTypes.TypeHandle(type.TypeId));
        HybridCpuManagedTypeMetadataArtifactV1 metadata =
            new HybridCpuManagedTypeMetadataEncoderV1().Encode([type]);
        Assert.Equal(HybridCpuManagedTypeMetadataStatusV1.Encoded, metadata.Status);
        byte[] image = new byte[256 + metadata.Bytes.Length];
        metadata.Bytes.CopyTo(image, 256);
        HybridCpuImageRuntimeBootstrapDescriptorV1 bootstrap = HybridCpuImageRuntimeBootstrapContractV1.Create(
            HybridCpuManagedAbiFamilyV1.Default.ContractDigest, "probe-entry", "probe-entry", [], [], [],
            managedTypes: [new(type.TypeId, type.StableIdentity, type.DescriptorDigest,
                256, metadata.Bytes.Length, null, typeHandle)]);
        HybridCpuIseManagedImageLoadRequestV1 request = new HybridCpuIseManagedImageLoadRequestV1(image, imageBase, imageBase,
            new string('a', 64), bootstrap,
            HybridCpuManagedHeapOptionsV1.Create(heapBase, 0x10000, 4096, -3),
            new string('b', 64), new string('c', 64), "test-pack");
        HybridCpuIseSparseMainMemoryAreaV1 memory = new HybridCpuIseSparseMainMemoryAreaV1();

        HybridCpuIseManagedImageLoadResultV1 result = new HybridCpuIseManagedImageLoaderV1().Load(
            request, memory, new DeterministicRuntimeKernelV1(),
            new Dictionary<string, HybridCpuRuntimeHelperEntryV1>());

        Assert.True(result.IsSuccess, result.Reason);
        Assert.False(result.HasExecutionAuthority);
        Assert.False(result.HasEcallAuthority);
        byte[] materialized = new byte[image.Length];
        Assert.True(memory.TryReadPhysicalRange(imageBase, materialized));
        Assert.Equal(image, materialized);
        ulong handle = Assert.IsType<ulong>(result.TypeSystem!.TypeHandle(type.TypeId));
        HybridCpuManagedHeapResultV1 allocated = result.Heap!.Allocate(handle);
        Assert.True(allocated.IsSuccess, allocated.Reason);
        Span<byte> header = stackalloc byte[8];
        Assert.True(memory.TryReadPhysicalRange(allocated.ObjectReference, header));
        Assert.Equal(handle, BinaryPrimitives.ReadUInt64LittleEndian(header));

        HybridCpuIseManagedImageLoadResultV1 rejected = new HybridCpuIseManagedImageLoaderV1().Load(
            request with { HeapOptions = HybridCpuManagedHeapOptionsV1.Create(imageBase, 4096, 4096, -3) },
            new HybridCpuIseSparseMainMemoryAreaV1(), new DeterministicRuntimeKernelV1(),
            new Dictionary<string, HybridCpuRuntimeHelperEntryV1>());
        Assert.Equal(HybridCpuIseManagedImageLoadStatusV1.MemoryRejected, rejected.Status);
    }

    [Fact]
    public void IseManagedImageLoader_BindsLdstrStaticRootToRuntimeOwnedLiteral()
    {
        const ulong imageBase = 0x0010_0000;
        const int metadataOffset = 256;
        const int rootOffset = 768;
        HybridCpuManagedTypeSystemBuildV1 sourceBuild = new HybridCpuManagedTypeSystemBuilderV1().Build(
        [
            new("System.String", HybridCpuManagedTypeKindV1.String, null, [], [], null,
                new(16, 20, 2, true))
        ]);
        Assert.True(sourceBuild.IsSuccess, sourceBuild.Reason);
        HybridCpuManagedTypeDescriptorV1 type = Assert.Single(sourceBuild.TypeSystem!.Descriptors);
        ulong typeHandle = Assert.IsType<ulong>(sourceBuild.TypeSystem.TypeHandle(type.TypeId));
        HybridCpuManagedTypeMetadataArtifactV1 metadata =
            new HybridCpuManagedTypeMetadataEncoderV1().Encode([type]);
        Assert.Equal(HybridCpuManagedTypeMetadataStatusV1.Encoded, metadata.Status);
        byte[] image = new byte[1024];
        metadata.Bytes.CopyTo(image, metadataOffset);
        const string rootIdentity = "__hybridcpu_managed_string_literal_root_0000000000000007";
        const string literalIdentity = "__hybridcpu_managed_string_literal_0000000000000007";
        HybridCpuImageRuntimeBootstrapDescriptorV1 bootstrap = HybridCpuImageRuntimeBootstrapContractV1.Create(
            HybridCpuManagedAbiFamilyV1.Default.ContractDigest, "probe-entry", "probe-entry",
            staticRoots: [new(rootIdentity, imageBase + rootOffset, sizeof(ulong))],
            managedTypes: [new(type.TypeId, type.StableIdentity, type.DescriptorDigest,
                metadataOffset, metadata.Bytes.Length, null, typeHandle)],
            stringLiterals: [new(literalIdentity, 7, type.TypeId, "runtime-owned")]);
        HybridCpuIseSparseMainMemoryAreaV1 memory = new HybridCpuIseSparseMainMemoryAreaV1();
        HybridCpuIseManagedImageLoadResultV1 loaded = new HybridCpuIseManagedImageLoaderV1().Load(
            new(image, imageBase, imageBase, new string('a', 64), bootstrap,
                HybridCpuManagedHeapOptionsV1.Create(0x2800_0000, 0x10000, 4096, -3),
                new string('b', 64), new string('c', 64), "test-pack"),
            memory, new DeterministicRuntimeKernelV1(),
            new Dictionary<string, HybridCpuRuntimeHelperEntryV1>());
        Assert.True(loaded.IsSuccess, loaded.Reason);
        Span<byte> rootBytes = stackalloc byte[sizeof(ulong)];
        Assert.True(memory.TryReadPhysicalRange(imageBase + rootOffset, rootBytes));
        ulong reference = BinaryPrimitives.ReadUInt64LittleEndian(rootBytes);
        Assert.Equal(loaded.Strings!.ResolveLiteral(7), reference);
        Assert.InRange(reference, 0x2800_0000UL, 0x2800_FFFFUL);
        Assert.Equal(13, loaded.Strings.Length(reference).ScalarValue);
    }

    [Fact]
    public void BumpAllocator_AlignsDoesNotOverlapZerosAndInitializesExactHeader()
    {
        HybridCpuManagedTypeSystemV1 types = Types(Field("value", 4, 4));
        HybridCpuManagedTypeDescriptorV1 type = Assert.Single(types.Descriptors);
        ulong typeHandle = Assert.IsType<ulong>(types.TypeHandle(type.TypeId));
        Assert.Equal(1UL, typeHandle);
        HybridCpuManagedHeapAllocatorV1 allocator = Allocator(types, out _);

        Assert.True(allocator.Initialize().IsSuccess);
        HybridCpuManagedHeapResultV1 first = allocator.Allocate(typeHandle);
        HybridCpuManagedHeapResultV1 second = allocator.Allocate(typeHandle);

        Assert.True(first.IsSuccess);
        Assert.True(second.IsSuccess);
        Assert.Equal(0UL, first.ObjectReference % (ulong)HybridCpuPlatformContractV1.ManagedObjectAlignmentBytes);
        Assert.Equal(first.ObjectReference + (ulong)first.ObjectSizeBytes, second.ObjectReference);
        byte[] bytes = Assert.IsType<byte[]>(allocator.ReadObjectBytes(first.ObjectReference));
        Assert.Equal(typeHandle, BinaryPrimitives.ReadUInt64LittleEndian(bytes));
        Assert.Equal(0UL, BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(8)));
        Assert.All(bytes.Skip(HybridCpuPlatformContractV1.ManagedObjectHeaderSizeBytes), static value => Assert.Equal(0, value));
    }

    [Fact]
    public void ConstructorPayloadWrite_PreservesHeaderAndRejectsOutOfObjectAccess()
    {
        HybridCpuManagedTypeSystemV1 types = Types(Field("value", 8, 8));
        HybridCpuManagedTypeDescriptorV1 type = Assert.Single(types.Descriptors);
        ulong typeHandle = Assert.IsType<ulong>(types.TypeHandle(type.TypeId));
        HybridCpuManagedHeapAllocatorV1 allocator = Allocator(types, out _);
        Assert.True(allocator.Initialize().IsSuccess);
        HybridCpuManagedHeapResultV1 allocated = allocator.Allocate(typeHandle);

        byte[] value = BitConverter.GetBytes(0x1122_3344_5566_7788UL);
        HybridCpuManagedHeapResultV1 write = allocator.WriteObjectBytes(
            allocated.ObjectReference, HybridCpuPlatformContractV1.ManagedObjectHeaderSizeBytes, value);
        HybridCpuManagedHeapResultV1 headerWrite = allocator.WriteObjectBytes(allocated.ObjectReference, 8, value);

        Assert.True(write.IsSuccess);
        Assert.Equal(HybridCpuManagedHeapStatusV1.InvalidObjectAccess, headerWrite.Status);
        byte[] bytes = allocator.ReadObjectBytes(allocated.ObjectReference)!;
        Assert.Equal(typeHandle, BinaryPrimitives.ReadUInt64LittleEndian(bytes));
        Assert.Equal(0x1122_3344_5566_7788UL,
            BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(HybridCpuPlatformContractV1.ManagedObjectHeaderSizeBytes)));
    }

    [Fact]
    public void Exhaustion_IsDeterministicDoesNotAdvanceBumpAndTerminatesThroughKernel()
    {
        HybridCpuManagedTypeSystemV1 types = Types(Field("payload", 4080, 8));
        HybridCpuManagedTypeDescriptorV1 type = Assert.Single(types.Descriptors);
        ulong typeHandle = Assert.IsType<ulong>(types.TypeHandle(type.TypeId));
        HybridCpuManagedHeapOptionsV1 options = HybridCpuManagedHeapOptionsV1.Create(0x4000_0000, 4096, 4096, -37);
        HybridCpuManagedHeapAllocatorV1 allocator = Allocator(types, out DeterministicRuntimeKernelV1 kernel, options);
        Assert.True(allocator.Initialize().IsSuccess);
        Assert.True(allocator.Allocate(typeHandle).IsSuccess);
        ulong bump = allocator.NextAddress;

        HybridCpuManagedHeapResultV1 exhausted = allocator.Allocate(typeHandle);

        Assert.Equal(HybridCpuManagedHeapStatusV1.OutOfMemory, exhausted.Status);
        Assert.Contains("process-exit:-37", exhausted.Reason, StringComparison.Ordinal);
        Assert.Equal(bump, allocator.NextAddress);
        Assert.Equal(HybridCpuKernelStatusV1.NoCurrentContext,
            kernel.ReserveVm(new(0x5000_0000, 4096, HybridCpuVmProtectionV1.None)).Status);
    }

    [Fact]
    public void MaximumObjectAndUnknownType_FailClosedWithoutMutation()
    {
        HybridCpuManagedTypeSystemV1 types = Types(Field("payload", 2048, 8));
        HybridCpuManagedTypeDescriptorV1 type = Assert.Single(types.Descriptors);
        ulong typeHandle = Assert.IsType<ulong>(types.TypeHandle(type.TypeId));
        HybridCpuManagedHeapOptionsV1 options = HybridCpuManagedHeapOptionsV1.Create(0x4000_0000, 8192, 1024, -3);
        HybridCpuManagedHeapAllocatorV1 allocator = Allocator(types, out _, options);
        Assert.True(allocator.Initialize().IsSuccess);
        ulong start = allocator.NextAddress;

        Assert.Equal(HybridCpuManagedHeapStatusV1.InvalidTypeDescriptor, allocator.Allocate(0).Status);
        Assert.Equal(HybridCpuManagedHeapStatusV1.ObjectTooLarge, allocator.Allocate(typeHandle).Status);
        Assert.Equal(start, allocator.NextAddress);
        Assert.Empty(allocator.Trace);
    }

    [Fact]
    public void KernelPermissionOrOverlappingRegionFailure_IsReportedWithoutHeapOwnership()
    {
        HybridCpuManagedTypeSystemV1 types = Types();
        DeterministicRuntimeKernelV1 kernel = BootKernel();
        HybridCpuManagedHeapOptionsV1 overlap = HybridCpuManagedHeapOptionsV1.Create(0x0010_0000, 4096, 4096, -3);
        HybridCpuManagedHeapAllocatorV1 allocator = new HybridCpuManagedHeapAllocatorV1(kernel, types, overlap);

        HybridCpuManagedHeapResultV1 result = allocator.Initialize();

        Assert.Equal(HybridCpuManagedHeapStatusV1.KernelFailure, result.Status);
        Assert.False(allocator.IsInitialized);
        Assert.Empty(allocator.Trace);
    }

    [Fact]
    public void AllocationTrace_IsByteDeterministicAcrossIndependentRuntimes()
    {
        HybridCpuManagedTypeSystemV1 firstTypes = Types(Field("a", 4, 4), Field("b", 8, 8));
        HybridCpuManagedTypeSystemV1 secondTypes = Types(Field("a", 4, 4), Field("b", 8, 8));
        HybridCpuManagedHeapAllocatorV1 first = Allocator(firstTypes, out _);
        HybridCpuManagedHeapAllocatorV1 second = Allocator(secondTypes, out _);
        Assert.True(first.Initialize().IsSuccess);
        Assert.True(second.Initialize().IsSuccess);
        ulong typeId = Assert.Single(firstTypes.Descriptors).TypeId;
        ulong firstHandle = Assert.IsType<ulong>(firstTypes.TypeHandle(typeId));
        ulong secondHandle = Assert.IsType<ulong>(secondTypes.TypeHandle(typeId));

        for (int index = 0; index < 32; index++)
        {
            Assert.True(first.Allocate(firstHandle).IsSuccess);
            Assert.True(second.Allocate(secondHandle).IsSuccess);
        }

        Assert.Equal(first.Trace, second.Trace);
        Assert.Equal(first.NextAddress, second.NextAddress);
        Assert.Equal(first.Trace.Select(static row => row.TraceDigest),
            second.Trace.Select(static row => row.TraceDigest));
    }

    [Fact]
    public void StaleOptionsAndCheckedTypeLayoutOverflow_FailClosed()
    {
        HybridCpuManagedTypeSystemV1 types = Types();
        HybridCpuManagedHeapOptionsV1 stale = HybridCpuManagedHeapOptionsV1.Production with
        {
            OptionsDigest = new string('0', 64)
        };
        HybridCpuManagedHeapAllocatorV1 allocator = Allocator(types, out _, stale);
        HybridCpuManagedTypeSystemBuildV1 overflow = new HybridCpuManagedTypeSystemBuilderV1().Build(
        [
            new("Overflow", HybridCpuManagedTypeKindV1.Class, null, [],
                [Field("huge", int.MaxValue, 1)])
        ]);

        Assert.Equal(HybridCpuManagedHeapStatusV1.InvalidConfiguration, allocator.Initialize().Status);
        Assert.Equal(HybridCpuManagedTypeSystemStatusV1.InvalidMetadata, overflow.Status);
        Assert.Contains("overflowed", overflow.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Newobj_LowersToAllocationThenConstructorAndPreservesReferenceAcrossCall()
    {
        string typeName = typeof(Phase03AllocatedFixture).FullName!;
        int constructorToken = typeof(Phase03AllocatedFixture).GetConstructor([typeof(int)])!.MetadataToken;
        HybridCpuManagedTypeSystemV1 types = new HybridCpuManagedTypeSystemBuilderV1().Build(
        [
            new(typeName, HybridCpuManagedTypeKindV1.Class, null, [],
                [new(nameof(Phase03AllocatedFixture.Value), HybridCpuManagedStorageKindV1.Primitive, 4, 4, false, 0)])
        ]).TypeSystem!;
        HybridCpuManagedTypeDescriptorV1 descriptor = Assert.Single(types.Descriptors);
        ulong typeHandle = Assert.IsType<ulong>(types.TypeHandle(descriptor.TypeId));
        RestrictedCilImporterV1 importer = new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2,
            fieldLayouts:
            [
                new(typeName, nameof(Phase03AllocatedFixture.Value), false, RestrictedCilTypeV1.Int32, descriptor)
            ],
            allocationBindings: [new(constructorToken, descriptor, typeHandle)]);
        byte[] pe = File.ReadAllBytes(typeof(Phase03AllocatedFixture).Assembly.Location);
        RestrictedCilMethodSelectorV1 create = new RestrictedCilMethodSelectorV1(typeName, nameof(Phase03AllocatedFixture.Create));
        RestrictedCilMethodSelectorV1 constructor = new RestrictedCilMethodSelectorV1(typeName, ".ctor", constructorToken);

        ManagedCallGraphCompilationV1 compilation = importer.ImportBodyWorld(new(
            ManagedBodyWorldModeV1.StandaloneRestrictedModule, pe, "phase03-newobj", [create], [create, constructor]));

        Assert.True(compilation.Status == RestrictedCilImportStatusV1.Success,
            string.Join(Environment.NewLine, compilation.Diagnostics.Select(static diagnostic =>
                $"{diagnostic.Code}: {diagnostic.Message}")));
        Assert.Equal(2, compilation.Methods.Count);
        Assert.Single(compilation.Graph!.Edges);
        ManagedCompiledMethodV1 root = compilation.Methods.Single(method => method.Identity.MethodName == nameof(Phase03AllocatedFixture.Create));
        IrProgram program = Assert.IsType<IrProgram>(root.Import.Program);
        IrInstruction allocationCall = Assert.Single(program.Instructions, static instruction =>
            instruction.Annotation.BranchTargetSymbolName == "__hybridcpu_managed_alloc");
        IrInstruction constructorCall = Assert.Single(program.Instructions, instruction =>
            instruction.Annotation.BranchTargetSymbolName == compilation.Graph.Edges[0].CalleeIdentity);
        Assert.Equal(IrMemoryEffectKind.Write, allocationCall.SideEffects.Memory.Kind);
        Assert.True(program.Instructions.ToList().IndexOf(allocationCall) <
            program.Instructions.ToList().IndexOf(constructorCall));

        IrProgramSchedule schedule = new HybridCpuLocalListScheduler().ScheduleProgram(program);
        IrProgramBundlingResult bundles = new HybridCpuBundleFormer().BundleProgram(schedule);
        IrRegisterAllocationResultV1 allocation = new HybridCpuScheduleAwareRegisterAllocatorV1().Allocate(
            schedule, bundles, resourceModel: ResourceModel,
            options: HybridCpuRegisterAllocationOptionsV1.Qualification);
        Assert.Equal(IrRegisterAllocationStatusV1.Allocated, allocation.Status);
        Assert.Contains(allocation.Witness!.SemanticValues, static value =>
            value.ValueId.Contains("allocated-object", StringComparison.Ordinal) &&
            value.ValueKind.Kind == IrCanonicalValueKind.ManagedObjectReference);
        Assert.Contains(allocation.Witness.Assignments, static value =>
            value.ValueId.Contains("allocated-object", StringComparison.Ordinal) && value.LiveAcrossCall);
        IrInstruction resultCopy = Assert.Single(program.Instructions, static instruction =>
            instruction.StableIdentity.EndsWith(":ctor-result-copy", StringComparison.Ordinal));
        Assert.True(program.Instructions.ToList().IndexOf(constructorCall) <
            program.Instructions.ToList().IndexOf(resultCopy));
        Assert.Contains(resultCopy.Annotation.Uses, static operand =>
            operand.Name.Contains("allocated-object", StringComparison.Ordinal));
        Assert.Contains(resultCopy.Annotation.Defs, static operand =>
            operand.Name.EndsWith(":value", StringComparison.Ordinal));
    }

    [Fact]
    public void Newobj_ResultSurvivesNeighborAllocationAndConstructorCalls()
    {
        string typeName = typeof(Phase03AllocatedFixture).FullName!;
        int constructorToken = typeof(Phase03AllocatedFixture).GetConstructor([typeof(int)])!.MetadataToken;
        HybridCpuManagedTypeSystemV1 types = new HybridCpuManagedTypeSystemBuilderV1().Build(
        [
            new(typeName, HybridCpuManagedTypeKindV1.Class, null, [],
                [new(nameof(Phase03AllocatedFixture.Value), HybridCpuManagedStorageKindV1.Primitive, 4, 4, false, 0)])
        ]).TypeSystem!;
        HybridCpuManagedTypeDescriptorV1 descriptor = Assert.Single(types.Descriptors);
        ulong typeHandle = Assert.IsType<ulong>(types.TypeHandle(descriptor.TypeId));
        RestrictedCilImporterV1 importer = new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2,
            fieldLayouts: [new(typeName, nameof(Phase03AllocatedFixture.Value), false, RestrictedCilTypeV1.Int32, descriptor)],
            allocationBindings: [new(constructorToken, descriptor, typeHandle)]);
        byte[] pe = File.ReadAllBytes(typeof(Phase03AllocatedFixture).Assembly.Location);
        RestrictedCilMethodSelectorV1 create = new RestrictedCilMethodSelectorV1(typeName, nameof(Phase03AllocatedFixture.CreateBeforeNeighbor));
        RestrictedCilMethodSelectorV1 constructor = new RestrictedCilMethodSelectorV1(typeName, ".ctor", constructorToken);
        ManagedCallGraphCompilationV1 compilation = importer.ImportBodyWorld(new(
            ManagedBodyWorldModeV1.StandaloneRestrictedModule, pe, "phase03-newobj-neighbor", [create], [create, constructor]));

        Assert.Equal(RestrictedCilImportStatusV1.Success, compilation.Status);
        IrProgram program = Assert.IsType<IrProgram>(compilation.Methods.Single(method =>
            method.Identity.MethodName == nameof(Phase03AllocatedFixture.CreateBeforeNeighbor)).Import.Program);
        IrInstruction[] resultCopies = program.Instructions.Where(static instruction =>
            instruction.StableIdentity.EndsWith(":ctor-result-copy", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, resultCopies.Length);
        string firstResult = Assert.Single(resultCopies[0].Annotation.Defs).Name;
        IrProgramSchedule schedule = new HybridCpuLocalListScheduler().ScheduleProgram(program);
        IrRegisterAllocationResultV1 allocation = new HybridCpuScheduleAwareRegisterAllocatorV1().Allocate(
            schedule, new HybridCpuBundleFormer().BundleProgram(schedule), resourceModel: ResourceModel,
            options: HybridCpuRegisterAllocationOptionsV1.Qualification);
        Assert.Equal(IrRegisterAllocationStatusV1.Allocated, allocation.Status);
        Assert.Contains(allocation.Witness!.Assignments, assignment =>
            assignment.ValueId == firstResult && assignment.LiveAcrossCall);
    }

    [Fact]
    public void Newobj_WithoutExactRuntimeBindingFailsClosedWithStableDiagnostic()
    {
        string typeName = typeof(Phase03AllocatedFixture).FullName!;
        byte[] pe = File.ReadAllBytes(typeof(Phase03AllocatedFixture).Assembly.Location);
        RestrictedCilImporterV1 importer = new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2);

        RestrictedCilImportResultV1 result = importer.ImportImage(pe,
            new(typeName, nameof(Phase03AllocatedFixture.Create)), "phase03-newobj-negative");

        Assert.Equal(RestrictedCilImportStatusV1.Unsupported, result.Status);
        Assert.True(result.Diagnostics.Count == 1 && result.Diagnostics[0].Code == "HCCIL1301",
            string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic =>
                $"{diagnostic.Code}: {diagnostic.Message}")));
        Assert.Contains($"{typeName}..ctor", result.Diagnostics[0].Message, StringComparison.Ordinal);
        Assert.Contains("token 0x", result.Diagnostics[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RealImage_BootstrapAllocationHelperAndConstructorExecuteOnIse()
    {
        const string managedEntry = "phase03_managed_entry";
        const string allocationHelper = "__hybridcpu_managed_alloc";
        const string constructor = "phase03_object_ctor";
        const ushort fieldValue = 73;
        HybridCpuManagedTypeSystemV1 types = Types(Field("value", 4, 4));
        HybridCpuManagedTypeDescriptorV1 type = Assert.Single(types.Descriptors);
        ulong typeHandle = Assert.IsType<ulong>(types.TypeHandle(type.TypeId));
        HybridCpuStaticLinkArtifactV1 link = LinkAllocationImage(
            managedEntry, allocationHelper, constructor, checked((ushort)typeHandle), fieldValue);
        HybridCpuRuntimeHelperV1 helperAbi = Assert.IsType<HybridCpuRuntimeHelperV1>(
            HybridCpuManagedAbiFamilyV1.Default.ResolveRuntimeHelper(allocationHelper));
        HybridCpuImageRuntimeBootstrapDescriptorV1 descriptor = HybridCpuImageRuntimeBootstrapContractV1.Create(
            HybridCpuManagedAbiFamilyV1.Default.ContractDigest, managedEntry, managedEntry,
            [new(allocationHelper, helperAbi.Signature, true)], managedTypes:
            [new(type.TypeId, type.StableIdentity, type.DescriptorDigest, 0, 1, null)]);
        HybridCpuRestrictedImageBuilderV1 imageBuilder = new HybridCpuRestrictedImageBuilderV1();
        HybridCpuRestrictedImageV1 image = imageBuilder.Inspect(imageBuilder.Build(
            new(link, managedEntry, RuntimeBootstrap: descriptor)).PackageBytes);
        Assert.Equal(HybridCpuStartupStatusV1.Success, image.Status);

        DeterministicRuntimeKernelV1 kernel = new DeterministicRuntimeKernelV1();
        HybridCpuKernelBootResultV1 boot = kernel.Boot(new(HybridCpuPlatformContractV1.ContractDigest,
            image.OptionsDigest, image.ImageBase, AlignUp((ulong)image.ImageBytes.Length, 4096), image.EntryAddress,
            HybridCpuRestrictedStartupOptionsV1.Production.StackBase,
            HybridCpuRestrictedStartupOptionsV1.Production.StackSize, 0));
        HybridCpuExecutionContextDescriptorV1 context = Assert.IsType<HybridCpuExecutionContextDescriptorV1>(boot.Context);
        HybridCpuManagedHeapOptionsV1 heapOptions = HybridCpuManagedHeapOptionsV1.Create(0x4000, 4096, 4096, -3);
        HybridCpuManagedHeapAllocatorV1 allocator = new HybridCpuManagedHeapAllocatorV1(kernel, types, heapOptions);
        Assert.True(allocator.Initialize().IsSuccess);
        HybridCpuManagedHeapResultV1 allocated = allocator.Allocate(typeHandle);
        Assert.True(allocated.IsSuccess, allocated.Reason);
        Assert.Equal(0x4000UL, allocated.ObjectReference);

        int operationalHelperBootstrapInvocations = 0;
        HybridCpuManagedBootstrapResultV1 bootstrap = new HybridCpuManagedBootstrapRuntimeV1(
            HybridCpuManagedAbiFamilyV1.Default.ContractDigest).Bootstrap(
            Assert.IsType<HybridCpuImageRuntimeBootstrapDescriptorV1>(image.RuntimeBootstrap), kernel,
            new Dictionary<string, HybridCpuRuntimeHelperEntryV1>(StringComparer.Ordinal)
            {
                [allocationHelper] = _ => { operationalHelperBootstrapInvocations++; return false; }
            }, typeSystem: types);
        Assert.True(bootstrap.IsSuccess, bootstrap.Reason);
        Assert.Equal(0, operationalHelperBootstrapInvocations);
        Assert.Equal([type.StableIdentity], bootstrap.RegisteredTypes);

        HybridCpuLinkedSymbolV1 helper = link.Symbols.Single(row => row.Name == allocationHelper);
        HybridCpuLinkedSymbolV1 ctor = link.Symbols.Single(row => row.Name == constructor);
        Processor.MainMemoryArea originalMemory = Processor.MainMemory;
        ProcessorMode originalMode = Processor.CurrentProcessorMode;
        MemorySubsystem? originalSubsystem = Processor.Memory;
        try
        {
            Processor.CurrentProcessorMode = ProcessorMode.Compiler;
            Processor.Memory = null;
            HybridCpuIseSparseMainMemoryAreaV1 memory = new HybridCpuIseSparseMainMemoryAreaV1();
            Processor.MainMemory = memory;
            Assert.True(memory.TryWritePhysicalRange(allocated.ObjectReference,
                Assert.IsType<byte[]>(allocator.ReadObjectBytes(allocated.ObjectReference))));
            Processor.CPU_Core core = new Processor.CPU_Core(0,
                CpuCorePlatformContext.CreateFixed(Processor.MainMemory, ProcessorMode.Compiler));
            core.InitializePipeline();
            core.PrepareExecutionStart(image.EntryAddress);
            for (int register = 0; register < 32; register++) core.WriteCommittedArch(0, register, 0);
            core.WriteCommittedPc(0, image.EntryAddress);
            core.WriteCommittedArch(0, HybridCpuNativeAbiContractV2.StackPointerRegister, context.InitialStackPointer);
            core.WriteCommittedArch(0, HybridCpuNativeAbiContractV2.ThreadPointerRegister, context.ContextCarrierAddress);

            ulong bundle = (ulong)HybridCpuBundleSerializer.BundleSizeBytes;
            RetireBundle(core, ReadBundle(image, image.EntryAddress), image.EntryAddress);
            Assert.Equal(typeHandle, core.ReadArch(0, 10));
            RetireBundle(core, ReadBundle(image, image.EntryAddress + bundle), image.EntryAddress + bundle);
            Assert.Equal((ulong)fieldValue, core.ReadArch(0, 11));
            RetireBundle(core, ReadBundle(image, image.EntryAddress + 2 * bundle), image.EntryAddress + 2 * bundle);
            Assert.Equal(helper.Address, core.ReadCommittedPc(0));
            Assert.Equal(image.EntryAddress + 2 * bundle + HybridCpuNativeCallControlContractV1.LinkIncrementBytes,
                core.ReadArch(0, HybridCpuNativeAbiContractV2.ReturnAddressRegister));
            RetireBundle(core, ReadBundle(image, helper.Address), helper.Address);
            Assert.Equal(allocated.ObjectReference, core.ReadArch(0, 10));
            RetireBundle(core, ReadBundle(image, helper.Address + bundle), helper.Address + bundle);
            Assert.Equal(image.EntryAddress + 3 * bundle, core.ReadCommittedPc(0));
            RetireBundle(core, ReadBundle(image, image.EntryAddress + 3 * bundle), image.EntryAddress + 3 * bundle);
            Assert.Equal(ctor.Address, core.ReadCommittedPc(0));
            RetireBundle(core, ReadBundle(image, ctor.Address), ctor.Address);
            RetireBundle(core, ReadBundle(image, ctor.Address + bundle), ctor.Address + bundle);
            Assert.Equal(image.EntryAddress + 4 * bundle, core.ReadCommittedPc(0));
            RetireBundle(core, ReadBundle(image, image.EntryAddress + 4 * bundle), image.EntryAddress + 4 * bundle);

            Assert.Equal(allocated.ObjectReference, core.ReadArch(0, 10));
            Assert.Equal(typeHandle, BinaryPrimitives.ReadUInt64LittleEndian(ReadPhysical(memory, allocated.ObjectReference, 8)));
            Assert.Equal((ulong)fieldValue, BinaryPrimitives.ReadUInt32LittleEndian(ReadPhysical(memory, allocated.ObjectReference + 16, 4)));
            Assert.Equal(context.ContextCarrierAddress,
                core.ReadArch(0, HybridCpuNativeAbiContractV2.ThreadPointerRegister));
        }
        finally
        {
            Processor.MainMemory = originalMemory;
            Processor.CurrentProcessorMode = originalMode;
            Processor.Memory = originalSubsystem;
        }
    }

    internal static HybridCpuStaticLinkArtifactV1 LinkAllocationImage(
        string managedEntry,
        string allocationHelper,
        string constructor,
        ushort typeHandle,
        ushort fieldValue)
    {
        HybridCpuBundleSerializer serializer = new HybridCpuBundleSerializer();
        byte[] managed = serializer.SerializeProgram([
            Bundle(AddImmediate(10, 0, typeHandle)),
            Bundle(AddImmediate(11, 0, fieldValue)),
            Bundle(Call()),
            Bundle(Call()),
            Bundle(AddImmediate(10, 10, 0))]);
        byte[] helper = serializer.SerializeProgram([
            Bundle(AddImmediate(10, 0, 0x4000)),
            Bundle(Return())]);
        byte[] ctor = serializer.SerializeProgram([
            Bundle(StoreWord(10, 11, 16)),
            Bundle(Return())]);
        HybridCpuObjectWriterV1 writer = new HybridCpuObjectWriterV1();
        byte[] Object(byte[] code, IReadOnlyList<HybridCpuObjectSymbolV1> symbols,
            IReadOnlyList<HybridCpuObjectRelocationV1> relocations)
        {
            HybridCpuObjectArtifactV1 artifact = writer.Write(new(
                [new(".text", HybridCpuObjectSectionKind.Code, HybridCpuBundleSerializer.BundleSizeBytes,
                    code, (ulong)code.Length)], symbols, relocations,
                HybridCpuTargetPlatformContractV1.Default.ContractDigest,
                HybridCpuManagedAbiFamilyV1.Default.ContractDigest));
            Assert.Equal(HybridCpuObjectStatusV1.Success, artifact.Status);
            return artifact.Bytes;
        }
        HybridCpuObjectSymbolV1 Definition(string name, ulong size) => new(name, HybridCpuSymbolBinding.Global,
            HybridCpuSymbolVisibility.Default, ".text", 0, size, true);
        HybridCpuObjectSymbolV1 Declaration(string name) => new(name, HybridCpuSymbolBinding.Global,
            HybridCpuSymbolVisibility.Default, null, 0, 0, false);
        ulong bundleSize = (ulong)HybridCpuBundleSerializer.BundleSizeBytes;
        HybridCpuStaticLinkArtifactV1 link = new HybridCpuStaticLinkerV1().Link([
            new("a-managed", Object(managed,
                [Definition(managedEntry, (ulong)managed.Length), Declaration(allocationHelper), Declaration(constructor)],
                [new(".text", 2 * bundleSize, HybridCpuRelocationKind.ManagedCallRelativeSigned16, allocationHelper, 0),
                    new(".text", 3 * bundleSize, HybridCpuRelocationKind.ManagedCallRelativeSigned16, constructor, 0)])),
            new("b-allocation", Object(helper, [Definition(allocationHelper, (ulong)helper.Length)], [])),
            new("c-constructor", Object(ctor, [Definition(constructor, (ulong)ctor.Length)], []))]);
        Assert.Equal(HybridCpuLinkStatusV1.Success, link.Status);
        return link;
    }

    private static HybridCpuInstructionBundle Bundle(params HybridCpuInstructionWord[] instructions)
    {
        HybridCpuInstructionBundle bundle = new HybridCpuInstructionBundle();
        for (int index = 0; index < instructions.Length; index++) bundle.SetInstruction(index, instructions[index]);
        return bundle;
    }

    private static HybridCpuInstructionWord AddImmediate(byte destination, byte source, ushort immediate) => new()
    {
        OpCode = (uint)HybridCpuOpcode.ADDI,
        DataTypeValue = HybridCpuDataType.INT64,
        PredicateMask = byte.MaxValue,
        Word1 = HybridCpuInstructionWord.PackArchRegs(destination, source, HybridCpuInstructionWord.NoArchReg),
        Immediate = immediate,
        VirtualThreadId = 0
    };

    private static HybridCpuInstructionWord Call() => new()
    {
        OpCode = (uint)HybridCpuOpcode.JAL,
        DataTypeValue = HybridCpuDataType.INT64,
        PredicateMask = byte.MaxValue,
        Word1 = HybridCpuInstructionWord.PackArchRegs(
            (byte)HybridCpuNativeAbiContractV2.ReturnAddressRegister,
            HybridCpuInstructionWord.NoArchReg, HybridCpuInstructionWord.NoArchReg),
        VirtualThreadId = 0
    };

    private static HybridCpuInstructionWord Return() => new()
    {
        OpCode = (uint)HybridCpuOpcode.JALR,
        DataTypeValue = HybridCpuDataType.INT64,
        PredicateMask = byte.MaxValue,
        Word1 = HybridCpuInstructionWord.PackArchRegs(0,
            (byte)HybridCpuNativeAbiContractV2.ReturnAddressRegister, HybridCpuInstructionWord.NoArchReg),
        Immediate = HybridCpuNativeCallControlContractV1.ManagedReturnAdjustmentBytes,
        VirtualThreadId = 0
    };

    private static HybridCpuInstructionWord StoreWord(byte address, byte source, ushort offset) => new()
    {
        OpCode = (uint)HybridCpuOpcode.SW,
        DataTypeValue = HybridCpuDataType.INT32,
        PredicateMask = byte.MaxValue,
        Word1 = HybridCpuInstructionWord.PackArchRegs(HybridCpuInstructionWord.NoArchReg, address, source),
        Immediate = offset,
        VirtualThreadId = 0
    };

    internal static VLIW_Instruction[] ReadBundle(HybridCpuRestrictedImageV1 image, ulong address)
    {
        int bundleIndex = checked((int)((address - image.ImageBase) / HybridCpuBundleSerializer.BundleSizeBytes));
        VLIW_Bundle bundle = new VLIW_Bundle();
        Assert.True(bundle.TryReadBytes(image.ImageBytes, bundleIndex * HybridCpuBundleSerializer.BundleSizeBytes));
        return Enumerable.Range(0, HybridCpuInstructionBundle.SlotCount).Select(bundle.GetInstruction).ToArray();
    }

    internal static void RetireBundle(Processor.CPU_Core core, VLIW_Instruction[] bundle, ulong pc)
    {
        bool control = bundle.Any(static instruction => instruction.OpCode is
            >= (uint)Processor.CPU_Core.InstructionsEnum.JAL and <= (uint)Processor.CPU_Core.InstructionsEnum.BGEU);
        core.TestRunDecodeStageWithFetchedBundle(bundle, pc);
        core.TestRunExecuteStageFromCurrentDecodeState();
        core.TestRunMemoryAndWriteBackStagesFromCurrentExecuteState();
        if (!control) core.WriteCommittedPc(0, pc);
    }

    internal static ulong AlignUp(ulong value, ulong alignment) =>
        checked((value + alignment - 1) / alignment * alignment);

    private static HybridCpuManagedHeapAllocatorV1 Allocator(
        HybridCpuManagedTypeSystemV1 types,
        out DeterministicRuntimeKernelV1 kernel,
        HybridCpuManagedHeapOptionsV1? options = null)
    {
        kernel = BootKernel();
        return new(kernel, types, options);
    }

    private static DeterministicRuntimeKernelV1 BootKernel()
    {
        DeterministicRuntimeKernelV1 kernel = new DeterministicRuntimeKernelV1();
        HybridCpuKernelBootResultV1 boot = kernel.Boot(new(
            HybridCpuPlatformContractV1.ContractDigest, new string('a', 64),
            0x0010_0000, 4096, 0x0010_0000,
            0x0020_0000, 4096, 0));
        Assert.True(boot.IsSuccess, boot.Reason);
        return kernel;
    }

    private static HybridCpuManagedTypeSystemV1 Types(params HybridCpuManagedFieldDeclarationV1[] fields)
    {
        HybridCpuManagedFieldDeclarationV1[] ordered = fields.Select((field, ordinal) =>
            field with { MetadataOrdinal = ordinal }).ToArray();
        HybridCpuManagedTypeSystemBuildV1 build = new HybridCpuManagedTypeSystemBuilderV1().Build(
        [
            new("Test.Object", HybridCpuManagedTypeKindV1.Class, null, [], ordered)
        ]);
        Assert.True(build.IsSuccess, build.Reason);
        return build.TypeSystem!;
    }

    private static HybridCpuManagedFieldDeclarationV1 Field(string identity, int size, int alignment) =>
        new(identity, HybridCpuManagedStorageKindV1.Primitive, size, alignment, false, 0);

}

public sealed class Phase03AllocatedFixture
{
    public int Value;

    public Phase03AllocatedFixture(int value) => Value = value;

    public static Phase03AllocatedFixture Create(int value) => new(value);

    public static Phase03AllocatedFixture CreateBeforeNeighbor(int value)
    {
        Phase03AllocatedFixture first = new(value);
        _ = new Phase03AllocatedFixture(value + 1);
        return first;
    }
}
