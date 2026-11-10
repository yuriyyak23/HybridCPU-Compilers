using HybridCPU_ISE.CloseToHSL.Core.Runtime.Managed;
using System.Buffers.Binary;
using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.Target;
using HybridCPU.Compiler.Core.Target.Link;
using HybridCPU.Compiler.Core.Target.Managed;
using HybridCPU.Compiler.Core.Target.Runtime;
using HybridCPU.ManagedRuntime;
using HybridCPU.Platform.Contracts;
using HybridCPU.RuntimeKernel;
using HybridCPU_ISE.Arch;
using YAKSys_Hybrid_CPU;
using YAKSys_Hybrid_CPU.Memory;

namespace HybridCPU_ISE.Tests.CompilerTests;

public sealed class CompilerRefPlan7Phase05NonMovingGcTests
{
    [Fact]
    public void OriginalImageRootsReadCurrentBackingAndRefuseLossBeforeReclaim()
    {
        HybridCpuManagedTypeSystemV1 types = Types(); HybridCpuManagedHeapAllocatorV1 heap = Heap(types); HybridCpuManagedNonMovingGcV1 gc = Gc(types, heap);
        ulong original = Allocate(heap, types, Type(types, "Node"));
        byte[]? slot = new byte[8];
        Assert.True(gc.BindImageStaticRoots([new("image-eh-root", 0x1000, 8)], (_, _) => slot));
        Assert.False(gc.BindImageStaticRoots([], (_, _) => []));
        BinaryPrimitives.WriteUInt64LittleEndian(slot, original);
        HybridCpuManagedNonMovingGcResultV1 retained = gc.Collect(new([], [], []), HybridCpuManagedNonMovingGcOptionsV1.Qualification);
        Assert.True(retained.IsSuccess, retained.Reason);
        Assert.Contains(original, retained.Roots);
        Assert.Empty(retained.ReclaimedObjects);
        slot = null;
        Assert.Equal(HybridCpuManagedNonMovingGcStatusV1.InvalidState,
            gc.Collect(new([], [], []), HybridCpuManagedNonMovingGcOptionsV1.Qualification).Status);
        slot = new byte[8]; BinaryPrimitives.WriteUInt64LittleEndian(slot, original);
        Assert.Contains(original, gc.Collect(new([], [], []), HybridCpuManagedNonMovingGcOptionsV1.Qualification).Roots);
        Array.Clear(slot);
        Assert.Contains(original, gc.Collect(new([], [], []), HybridCpuManagedNonMovingGcOptionsV1.Qualification).ReclaimedObjects);
    }

    [Theory]
    [InlineData(0UL, 8UL)] [InlineData(0x1001UL, 8UL)]
    [InlineData(0x1000UL, 7UL)] [InlineData(0x1000UL, 131080UL)]
    [InlineData(0xfffffffffffffff8UL, 16UL)]
    public void ImageRootBindingRejectsMalformedRangesBeforeRead(ulong address, ulong size)
    {
        HybridCpuManagedTypeSystemV1 types = Types(); HybridCpuManagedNonMovingGcV1 gc = Gc(types, Heap(types)); int reads = 0;
        Assert.False(gc.BindImageStaticRoots([new("bad", address, size)], (_, _) => { reads++; return []; }));
        Assert.Equal(0, reads);
        Assert.True(gc.BindImageStaticRoots([], (_, _) => []));
    }

    [Fact]
    public void ImageRootBindingCannotBeginAfterCollectionOrImpersonateExplicitStaticOwner()
    {
        HybridCpuManagedTypeSystemV1 types = Types(); HybridCpuManagedNonMovingGcV1 gc = Gc(types, Heap(types));
        Assert.True(gc.Collect(new([], [], []), HybridCpuManagedNonMovingGcOptionsV1.Qualification).IsSuccess);
        Assert.False(gc.BindImageStaticRoots([], (_, _) => []));
        Assert.Equal(HybridCpuManagedNonMovingGcStatusV1.InvalidState, gc.Collect(
            new([], [], [new("foreign-static", HybridCpuManagedGcRootSourceV1.Static, 0)]),
            HybridCpuManagedNonMovingGcOptionsV1.Qualification).Status);
    }

    [Fact]
    public void ImageRootReadReentryAndThrowBalanceOriginalCollectionWithoutReclaim()
    {
        HybridCpuManagedTypeSystemV1 types = Types(); HybridCpuManagedHeapAllocatorV1 heap = Heap(types); HybridCpuManagedNonMovingGcV1 gc = Gc(types, heap);
        ulong original = Allocate(heap, types, Type(types, "Node")); bool throwing = true;
        Assert.True(gc.BindImageStaticRoots([new("original", 0x1000, 8)], (_, _) =>
        {
            object gate = typeof(HybridCpuManagedNonMovingGcV1).GetField("_imageRootBindingGate",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(gc)!;
            Assert.False(Monitor.IsEntered(gate));
            Assert.False(gc.BindImageStaticRoots([], (_, _) => []));
            Assert.Equal(HybridCpuManagedNonMovingGcStatusV1.InvalidState,
                gc.Collect(new([], [], []), HybridCpuManagedNonMovingGcOptionsV1.Qualification).Status);
            Assert.Equal(HybridCpuManagedNonMovingGcStatusV1.InvalidState, Task.Run(() =>
                gc.Collect(new([], [], []), HybridCpuManagedNonMovingGcOptionsV1.Qualification)).GetAwaiter().GetResult().Status);
            if (throwing) throw new IOException("Original root read lost.");
            byte[] bytes = new byte[8]; BinaryPrimitives.WriteUInt64LittleEndian(bytes, original); return bytes;
        }));
        Assert.Throws<IOException>(() => gc.Collect(new([], [], []), HybridCpuManagedNonMovingGcOptionsV1.Qualification));
        throwing = false;
        HybridCpuManagedNonMovingGcResultV1 reconciled = gc.Collect(new([], [], []), HybridCpuManagedNonMovingGcOptionsV1.Qualification);
        Assert.True(reconciled.IsSuccess, reconciled.Reason);
        Assert.Contains(original, reconciled.Roots); Assert.Empty(reconciled.ReclaimedObjects);
    }

    [Fact]
    public void StackMapRegistrationAndFrameSnapshotAreNeutralPlatformPods()
    {
        Assert.Equal(13, HybridCpuPlatformContractV1.SchemaMinor);
        Assert.Equal(typeof(HybridCpuPlatformContractV1).Assembly,
            typeof(HybridCpuManagedStackMapRegistrationV1).Assembly);
        Assert.Equal(typeof(HybridCpuPlatformContractV1).Assembly,
            typeof(HybridCpuManagedGcFrameSnapshotV1).Assembly);
        Assert.NotEqual(typeof(HybridCpuManagedNonMovingGcV1).Assembly,
            typeof(HybridCpuManagedStackMapRegistrationV1).Assembly);
    }

    [Fact]
    public void FinalCallPolicy_DerivesCompleteLiveRegisterRootsDeterministically()
    {
        CompilerRefPlan5Phase25AManagedMetadataFinalizationTests.AllocatedSubject subject = CompilerRefPlan5Phase25AManagedMetadataFinalizationTests.AllocateSubject("root");
        HybridCpuManagedMetadataFinalizerV1 finalizer = new HybridCpuManagedMetadataFinalizerV1();

        HybridCpuManagedMetadataArtifactV1 first = finalizer.FinalizeRequiredCallSites(
            "Phase05.ManagedMethod", 0, subject.Result, HybridCpuManagedMetadataOptionsV1.Qualification);
        HybridCpuManagedMetadataArtifactV1 second = finalizer.FinalizeRequiredCallSites(
            "Phase05.ManagedMethod", 0, subject.Result, HybridCpuManagedMetadataOptionsV1.Qualification);

        Assert.Equal(HybridCpuManagedMetadataStatusV1.Finalized, first.Status);
        HybridCpuSafepointRecordV1 point = Assert.Single(first.Safepoints);
        Assert.Equal(HybridCpuSafepointCategoryV1.CallSite, point.Category);
        Assert.Equal("root", Assert.Single(point.LiveReferences).ValueIdentity);
        Assert.Equal(first.GcInfo, second.GcInfo);
        Assert.Equal(first.CodeManagerMetadata, second.CodeManagerMetadata);
        Assert.Equal(first.ResultDigest, second.ResultDigest);
        Assert.Contains("phase11-polls=explicit-managed-helper-sites",
            HybridCpuManagedMetadataContractV1.Default.Phase05SafepointPolicy, StringComparison.Ordinal);
    }

    [Fact]
    public void Collector_TraversesFinalFrameStaticObjectArrayBoxLiteralHandlePinAndTemporaryRoots()
    {
        HybridCpuManagedTypeSystemV1 types = Types();
        HybridCpuManagedHeapAllocatorV1 heap = Heap(types);
        HybridCpuManagedTypeDescriptorV1 node = Type(types, "Node");
        HybridCpuManagedTypeDescriptorV1 derived = Type(types, "Derived");
        HybridCpuManagedTypeDescriptorV1 refs = Type(types, "Node[]");
        HybridCpuManagedTypeDescriptorV1 boxed = Type(types, "RefBox");
        HybridCpuManagedTypeDescriptorV1 strings = Type(types, "System.String");
        HybridCpuManagedTypeDescriptorV1 statics = Type(types, "Statics");

        ulong child = Allocate(heap, types, node);
        ulong derivedRoot = Allocate(heap, types, derived);
        WriteRef(heap, derivedRoot, node.InstanceFields.Single().OffsetBytes, child);
        ulong arrayChild = Allocate(heap, types, node);
        ulong array = new HybridCpuManagedArrayRuntimeV1(types, heap)
            .NewArray(Handle(types, refs), 1).ObjectReference;
        Assert.True(new HybridCpuManagedArrayRuntimeV1(types, heap).StoreReference(array, 0, arrayChild).IsSuccess);
        ulong boxedChild = Allocate(heap, types, node);
        ulong box = heap.AllocateVariable(Handle(types, boxed),
            boxed.ValueTypeShape!.BoxedPayloadOffsetBytes + boxed.ValueTypeShape.PayloadSizeBytes).ObjectReference;
        WriteRef(heap, box, boxed.ValueTypeShape.BoxedPayloadOffsetBytes, boxedChild);
        ulong handle = Allocate(heap, types, node);
        ulong pinned = Allocate(heap, types, node);
        ulong temporary = Allocate(heap, types, node);
        ulong garbage = Allocate(heap, types, node);

        HybridCpuManagedStringRuntimeV1 stringRuntime = new HybridCpuManagedStringRuntimeV1(types, heap);
        ulong literal = stringRuntime.RegisterLiteral(7, Handle(types, strings), "gc-root").ObjectReference;
        int staticOffset = Assert.Single(statics.StaticLayout.ObjectReferenceOffsets);
        Assert.True(new HybridCpuManagedStaticFieldRuntimeV1(types)
            .StoreReference(Handle(types, statics), staticOffset, derivedRoot).IsSuccess);

        (byte[] gc, byte[] cm) = Metadata(("array", 10, null), ("box", null, 0));
        ulong[] registers = new ulong[64]; registers[10] = array;
        HybridCpuManagedGcRootRegistryV1 rootRegistry = new HybridCpuManagedGcRootRegistryV1();
        Assert.True(rootRegistry.Register("h", HybridCpuManagedGcRootSourceV1.Handle, handle));
        Assert.True(rootRegistry.Register("p", HybridCpuManagedGcRootSourceV1.Pinned, pinned));
        Assert.True(rootRegistry.Register("t", HybridCpuManagedGcRootSourceV1.HelperTemporary, temporary));
        HybridCpuManagedNonMovingGcRequestV1 request = new(
            [Registration(gc, cm)],
            [new("Phase05.Scan", 0, registers, new Dictionary<int, ulong> { [0] = box })],
            rootRegistry.Snapshot(), stringRuntime);

        HybridCpuManagedNonMovingGcResultV1 result = Gc(types, heap).Collect(
            request, HybridCpuManagedNonMovingGcOptionsV1.Qualification);

        Assert.True(result.IsSuccess, result.Reason);
        ulong[] expected = [child, derivedRoot, arrayChild, array, boxedChild, box, handle, pinned, temporary, literal];
        Assert.Equal(expected.Order(), result.ReachableObjects);
        Assert.Equal([garbage], result.ReclaimedObjects);
        Assert.Null(heap.ReadObjectBytes(garbage));
        Assert.All(expected, address => Assert.NotNull(heap.ReadObjectBytes(address)));
        Assert.False(Gc(types, heap).IsMoving);
        Assert.False(Gc(types, heap).RequiresWriteBarrier);
    }

    [Fact]
    public void Collector_ReclaimsThenFirstFitReusesAddressWithoutMovement()
    {
        HybridCpuManagedTypeSystemV1 types = Types();
        HybridCpuManagedHeapAllocatorV1 heap = Heap(types, 4096, 4096);
        HybridCpuManagedTypeDescriptorV1 boxed = Type(types, "RefBox");
        ulong handle = Handle(types, boxed);
        ulong first = heap.AllocateVariable(handle, 2000).ObjectReference;
        ulong live = heap.AllocateVariable(handle, 2000).ObjectReference;
        HybridCpuManagedNonMovingGcRequestV1 request = new([], [],
            [new("live", HybridCpuManagedGcRootSourceV1.Handle, live)]);

        HybridCpuManagedHeapResultV1 retry = Gc(types, heap).AllocateWithCollectionRetry(
            handle, 2000, request, HybridCpuManagedNonMovingGcOptionsV1.Qualification);

        Assert.True(retry.IsSuccess, retry.Reason);
        Assert.Equal(first, retry.ObjectReference);
        Assert.NotNull(heap.ReadObjectBytes(live));
        Assert.Equal(3, heap.Trace.Count);
        Assert.Empty(heap.FreeBlocks());
    }

    [Fact]
    public void RealImage_LoaderBootstrapIseSafepointAndRuntimeSweepUseFinalHcmgHcmm()
    {
        const string entry = "phase05_managed_entry";
        const string helperName = "__hybridcpu_managed_alloc";
        const string constructor = "phase05_object_ctor";
        HybridCpuManagedTypeSystemV1 types = Types();
        HybridCpuManagedTypeDescriptorV1 node = Type(types, "Node");
        ulong typeHandle = Handle(types, node);
        HybridCpuManagedHeapAllocatorV1 heap = Heap(types);
        ulong dead = Allocate(heap, types, node);
        ulong live = Allocate(heap, types, node);
        HybridCpuStaticLinkArtifactV1 link = CompilerRefPlan7Phase03ManagedHeapAllocatorTests.LinkAllocationImage(
            entry, helperName, constructor, checked((ushort)typeHandle), 73);
        HybridCpuLinkedSymbolV1 entrySymbol = link.Symbols.Single(row => row.Name == entry);
        int codeStart = checked((int)(entrySymbol.Address - link.ImageBase));
        int safepointOffset = checked(2 * HybridCpuBundleSerializer.BundleSizeBytes);
        (byte[] gcInfo, byte[] codeManager) = Metadata(entry, codeStart,
            checked((int)entrySymbol.Size), safepointOffset, ("live", 12, null));
        string unwindDigest = new string('0', 64);
        HybridCpuRuntimeHelperV1 helper = Assert.IsType<HybridCpuRuntimeHelperV1>(
            HybridCpuManagedAbiFamilyV1.Default.ResolveRuntimeHelper(helperName));
        HybridCpuImageRuntimeBootstrapDescriptorV1 descriptor = HybridCpuImageRuntimeBootstrapContractV1.Create(
            HybridCpuManagedAbiFamilyV1.Default.ContractDigest, entry, entry,
            [new(helperName, helper.Signature, true)],
            [new(entry, codeStart, checked((int)entrySymbol.Size),
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(gcInfo)).ToLowerInvariant(), unwindDigest)],
            managedTypes: [new(node.TypeId, node.StableIdentity, node.DescriptorDigest, 0, 1, null)]);
        HybridCpuRestrictedImageBuilderV1 imageBuilder = new HybridCpuRestrictedImageBuilderV1();
        HybridCpuRestrictedImageV1 image = imageBuilder.Inspect(imageBuilder.Build(
            new(link, entry, RuntimeBootstrap: descriptor)).PackageBytes);
        Assert.Equal(HybridCpuStartupStatusV1.Success, image.Status);

        DeterministicRuntimeKernelV1 kernel = new DeterministicRuntimeKernelV1();
        HybridCpuKernelBootResultV1 boot = kernel.Boot(new(HybridCpuPlatformContractV1.ContractDigest,
            image.OptionsDigest, image.ImageBase,
            CompilerRefPlan7Phase03ManagedHeapAllocatorTests.AlignUp((ulong)image.ImageBytes.Length, 4096),
            image.EntryAddress, HybridCpuRestrictedStartupOptionsV1.Production.StackBase,
            HybridCpuRestrictedStartupOptionsV1.Production.StackSize, 0));
        HybridCpuExecutionContextDescriptorV1 context = Assert.IsType<HybridCpuExecutionContextDescriptorV1>(boot.Context);
        HybridCpuManagedBootstrapResultV1 bootstrap = new HybridCpuManagedBootstrapRuntimeV1(
            HybridCpuManagedAbiFamilyV1.Default.ContractDigest).Bootstrap(
            Assert.IsType<HybridCpuImageRuntimeBootstrapDescriptorV1>(image.RuntimeBootstrap), kernel,
            new Dictionary<string, HybridCpuRuntimeHelperEntryV1> { [helperName] = _ => false },
            typeSystem: types);
        Assert.True(bootstrap.IsSuccess, bootstrap.Reason);
        Assert.Equal([entry], bootstrap.RegisteredMethods);

        Processor.MainMemoryArea originalMemory = Processor.MainMemory;
        ProcessorMode originalMode = Processor.CurrentProcessorMode;
        MemorySubsystem? originalSubsystem = Processor.Memory;
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
            core.WriteCommittedArch(0, HybridCpuNativeAbiContractV2.StackPointerRegister, context.InitialStackPointer);
            core.WriteCommittedArch(0, HybridCpuNativeAbiContractV2.ThreadPointerRegister, context.ContextCarrierAddress);
            core.WriteCommittedArch(0, 12, live);
            ulong bundle = (ulong)HybridCpuBundleSerializer.BundleSizeBytes;
            CompilerRefPlan7Phase03ManagedHeapAllocatorTests.RetireBundle(core,
                CompilerRefPlan7Phase03ManagedHeapAllocatorTests.ReadBundle(image, image.EntryAddress), image.EntryAddress);
            CompilerRefPlan7Phase03ManagedHeapAllocatorTests.RetireBundle(core,
                CompilerRefPlan7Phase03ManagedHeapAllocatorTests.ReadBundle(image, image.EntryAddress + bundle), image.EntryAddress + bundle);
            Assert.Equal(live, core.ReadArch(0, 12));
            ulong[] registers = Enumerable.Range(0, 64).Select(index => index < 32 ? core.ReadArch(0, index) : 0).ToArray();

            HybridCpuManagedNonMovingGcResultV1 collection = Gc(types, heap).Collect(new(
                [Registration(entry, gcInfo, codeManager)],
                [new(entry, checked(codeStart + safepointOffset), registers, new Dictionary<int, ulong>())], []),
                HybridCpuManagedNonMovingGcOptionsV1.Qualification);
            Assert.True(collection.IsSuccess, collection.Reason);
            Assert.Equal([live], collection.ReachableObjects);
            Assert.Equal([dead], collection.ReclaimedObjects);
            Assert.NotNull(heap.ReadObjectBytes(live));

            CompilerRefPlan7Phase03ManagedHeapAllocatorTests.RetireBundle(core,
                CompilerRefPlan7Phase03ManagedHeapAllocatorTests.ReadBundle(image, image.EntryAddress + 2 * bundle),
                image.EntryAddress + 2 * bundle);
            Assert.Equal(link.Symbols.Single(row => row.Name == helperName).Address, core.ReadCommittedPc(0));
        }
        finally
        {
            Processor.MainMemory = originalMemory;
            Processor.CurrentProcessorMode = originalMode;
            Processor.Memory = originalSubsystem;
        }
    }

    [Fact]
    public void CorruptStackMap_FailsTransactionallyWithoutSweep()
    {
        HybridCpuManagedTypeSystemV1 types = Types();
        HybridCpuManagedHeapAllocatorV1 heap = Heap(types);
        ulong value = Allocate(heap, types, Type(types, "Node"));
        (byte[] gc, byte[] cm) = Metadata(("root", 10, null));
        gc[0] ^= 0xff;
        ulong[] registers = new ulong[64]; registers[10] = value;

        HybridCpuManagedNonMovingGcResultV1 result = Gc(types, heap).Collect(new(
            [Registration(gc, cm)], [new("Phase05.Scan", 0, registers, new Dictionary<int, ulong>())], []),
            HybridCpuManagedNonMovingGcOptionsV1.Qualification);

        Assert.Equal(HybridCpuManagedNonMovingGcStatusV1.InvalidMetadata, result.Status);
        Assert.NotNull(heap.ReadObjectBytes(value));
        Assert.Empty(heap.FreeBlocks());
    }

    [Fact]
    public void UnknownRoot_FailsClosedAndProductionRemainsDefaultDisabled()
    {
        HybridCpuManagedTypeSystemV1 types = Types();
        HybridCpuManagedHeapAllocatorV1 heap = Heap(types);
        HybridCpuManagedNonMovingGcV1 gc = Gc(types, heap);
        HybridCpuManagedNonMovingGcRequestV1 request = new HybridCpuManagedNonMovingGcRequestV1([], [],
            [new("bad", HybridCpuManagedGcRootSourceV1.Handle, 0x1234)]);

        Assert.Equal(HybridCpuManagedNonMovingGcStatusV1.Disabled, gc.Collect(request).Status);
        Assert.Equal(HybridCpuManagedNonMovingGcStatusV1.InvalidState,
            gc.Collect(request, HybridCpuManagedNonMovingGcOptionsV1.Qualification).Status);
        HybridCpuManagedNonMovingGcResultV1 forgedStatic = gc.Collect(
            request with { Roots = [new("forged", HybridCpuManagedGcRootSourceV1.Static, 0x1234)] },
            HybridCpuManagedNonMovingGcOptionsV1.Qualification);
        Assert.Equal(HybridCpuManagedNonMovingGcStatusV1.InvalidState, forgedStatic.Status);
        Assert.Contains("static and literal roots", forgedStatic.Reason, StringComparison.Ordinal);
        Assert.True(gc.IsSingleContext);
        Assert.True(gc.IsStopTheWorld);
        Assert.False(gc.IsConcurrent);
        Assert.False(gc.IsGenerational);
        Assert.False(gc.SupportsInteriorReferences);
        Assert.True(gc.HasObjectLifetimeAuthority);
        Assert.False(gc.HasIseExecutionAuthority);
        Assert.Equal(64, gc.ContractDigest.Length);
    }

    [Fact]
    public void CollectionAndFreeListAreDeterministicAcrossIndependentRuntimes()
    {
        (HybridCpuManagedNonMovingGcResultV1 Result, HybridCpuManagedHeapAllocatorV1 Heap) first = RunDeterministic();
        (HybridCpuManagedNonMovingGcResultV1 Result, HybridCpuManagedHeapAllocatorV1 Heap) second = RunDeterministic();

        Assert.Equal(first.Result.Status, second.Result.Status);
        Assert.Equal(first.Result.ResultDigest, second.Result.ResultDigest);
        Assert.Equal(first.Result.Roots, second.Result.Roots);
        Assert.Equal(first.Result.ReachableObjects, second.Result.ReachableObjects);
        Assert.Equal(first.Result.ReclaimedObjects, second.Result.ReclaimedObjects);
        Assert.Equal(first.Result.FreeBlocks, second.Result.FreeBlocks);
        Assert.Equal(first.Heap.ActiveAllocations(), second.Heap.ActiveAllocations());
        Assert.Equal(first.Heap.FreeBlocks(), second.Heap.FreeBlocks());
        Assert.Equal(first.Heap.Trace, second.Heap.Trace);
    }

    private static (HybridCpuManagedNonMovingGcResultV1, HybridCpuManagedHeapAllocatorV1) RunDeterministic()
    {
        HybridCpuManagedTypeSystemV1 types = Types();
        HybridCpuManagedHeapAllocatorV1 heap = Heap(types);
        HybridCpuManagedTypeDescriptorV1 node = Type(types, "Node");
        ulong dead = Allocate(heap, types, node);
        ulong live = Allocate(heap, types, node);
        HybridCpuManagedNonMovingGcResultV1 result = Gc(types, heap).Collect(new([], [],
            [new("live", HybridCpuManagedGcRootSourceV1.Handle, live)]),
            HybridCpuManagedNonMovingGcOptionsV1.Qualification);
        Assert.Equal([dead], result.ReclaimedObjects);
        return (result, heap);
    }

    private static (byte[] Gc, byte[] CodeManager) Metadata(params (string Identity, int? Register, int? Stack)[] roots) =>
        Metadata("Phase05.Scan", 0, 8, 0, roots);

    private static (byte[] Gc, byte[] CodeManager) Metadata(string method, int codeStart, int codeSize,
        int safepointOffset, params (string Identity, int? Register, int? Stack)[] roots)
    {
        HybridCpuGcReferenceLocationV1[] locations = roots.Select(root => new HybridCpuGcReferenceLocationV1(
            root.Identity, HybridCpuGcReferenceKindV1.ObjectReference,
            root.Register is not null ? HybridCpuGcLocationKindV1.Register : HybridCpuGcLocationKindV1.Stack,
            root.Register, root.Stack)).ToArray();
        HybridCpuGcInfoEncodingResultV1 gc = HybridCpuManagedAbiEncodingV1.EncodeGcInfo(
            [new(safepointOffset, HybridCpuSafepointCategoryV1.CallSite, locations)]);
        Assert.Equal(HybridCpuPlatformFactStatus.Supported, gc.Status);
        byte[] codeManager = HybridCpuManagedAbiEncodingV1.EncodeCodeManagerMetadata(
            [new(method, codeStart, codeSize, gc.Digest, null)]);
        return (gc.Bytes, codeManager);
    }

    private static HybridCpuManagedStackMapRegistrationV1 Registration(byte[] gc, byte[] codeManager)
        => Registration("Phase05.Scan", gc, codeManager);

    private static HybridCpuManagedStackMapRegistrationV1 Registration(string method, byte[] gc, byte[] codeManager)
    {
        HybridCpuManagedAbiFamilyV1 abi = HybridCpuManagedAbiFamilyV1.Default;
        return new(method, gc, codeManager, abi.ContractDigest, abi.TargetContractDigest,
            abi.NativeAbiDigest, HybridCpuManagedAbiFamilyV1.RuntimePackRevision);
    }

    private static HybridCpuManagedNonMovingGcV1 Gc(HybridCpuManagedTypeSystemV1 types,
        HybridCpuManagedHeapAllocatorV1 heap)
    {
        HybridCpuManagedAbiFamilyV1 abi = HybridCpuManagedAbiFamilyV1.Default;
        return new(types, heap, abi.ContractDigest, abi.TargetContractDigest,
            abi.NativeAbiDigest, HybridCpuManagedAbiFamilyV1.RuntimePackRevision);
    }

    private static HybridCpuManagedHeapAllocatorV1 Heap(HybridCpuManagedTypeSystemV1 types,
        ulong size = 4096, int maximumObject = 4096)
    {
        DeterministicRuntimeKernelV1 kernel = new DeterministicRuntimeKernelV1();
        Assert.True(kernel.Boot(new(HybridCpuPlatformContractV1.ContractDigest, new string('a', 64),
            0x0010_0000, 4096, 0x0010_0000, 0x0020_0000, 4096, 0)).IsSuccess);
        HybridCpuManagedHeapAllocatorV1 heap = new HybridCpuManagedHeapAllocatorV1(kernel, types,
            HybridCpuManagedHeapOptionsV1.Create(0x4000_0000, size, maximumObject, -3));
        Assert.True(heap.Initialize().IsSuccess);
        return heap;
    }

    private static ulong Allocate(HybridCpuManagedHeapAllocatorV1 heap, HybridCpuManagedTypeSystemV1 types,
        HybridCpuManagedTypeDescriptorV1 type)
    {
        HybridCpuManagedHeapResultV1 result = heap.Allocate(Handle(types, type));
        Assert.True(result.IsSuccess, result.Reason);
        return result.ObjectReference;
    }

    private static void WriteRef(HybridCpuManagedHeapAllocatorV1 heap, ulong owner, int offset, ulong value)
    {
        Span<byte> bytes = stackalloc byte[8]; BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
        Assert.True(heap.WriteObjectBytes(owner, offset, bytes).IsSuccess);
    }

    private static ulong Handle(HybridCpuManagedTypeSystemV1 types, HybridCpuManagedTypeDescriptorV1 type) =>
        types.TypeHandle(type.TypeId)!.Value;

    private static HybridCpuManagedTypeDescriptorV1 Type(HybridCpuManagedTypeSystemV1 types, string identity) =>
        types.Descriptors.Single(row => row.StableIdentity == identity);

    private static HybridCpuManagedTypeSystemV1 Types()
    {
        ulong nodeId = StableTypeId("Node");
        HybridCpuManagedTypeSystemBuildV1 build = new HybridCpuManagedTypeSystemBuilderV1().Build(
        [
            new("Node", HybridCpuManagedTypeKindV1.Class, null, [],
                [new("child", HybridCpuManagedStorageKindV1.ObjectReference, 8, 8, false, 0)]),
            new("Derived", HybridCpuManagedTypeKindV1.Class, "Node", [],
                [new("other", HybridCpuManagedStorageKindV1.ObjectReference, 8, 8, false, 0)]),
            new("Node[]", HybridCpuManagedTypeKindV1.SzArray, null, [], [],
                new(HybridCpuManagedStorageKindV1.ObjectReference, nodeId, 8, 8, 16, 24, true, true)),
            new("System.String", HybridCpuManagedTypeKindV1.String, null, [], [], null,
                new(16, 24, 2, true)),
            new("RefBox", HybridCpuManagedTypeKindV1.ValueType, null, [],
                [new("value", HybridCpuManagedStorageKindV1.ObjectReference, 8, 8, false, 0)], null, null,
                new(8, 8, [0], 16)),
            new("Statics", HybridCpuManagedTypeKindV1.Class, null, [],
                [new("root", HybridCpuManagedStorageKindV1.ObjectReference, 8, 8, true, 0)])
        ]);
        Assert.True(build.IsSuccess, build.Reason);
        return build.TypeSystem!;
    }

    private static ulong StableTypeId(string identity)
    {
        byte[] hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes($"hybridcpu.managed-type-id/v1|{identity}"));
        ulong value = BinaryPrimitives.ReadUInt64LittleEndian(hash);
        return value == 0 ? 1 : value;
    }
}
