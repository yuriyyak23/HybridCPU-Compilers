using System.Buffers.Binary;
using System.Security.Cryptography;
using HybridCPU.Compiler.Cil;
using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.Target;
using HybridCPU.Compiler.Core.Target.Link;
using HybridCPU.Compiler.Core.Target.Managed;
using HybridCPU.Compiler.Core.Target.Object;
using HybridCPU.Compiler.Core.Target.Runtime;
using HybridCPU.ManagedRuntime;
using HybridCPU.Platform.Contracts;
using HybridCPU.RuntimeKernel;
using HybridCPU_ISE.CloseToHSL.Core.Runtime.Managed;

namespace HybridCPU_ISE.Tests.CompilerTests;

public sealed class CompilerRefPlan7Phase09IseEhExecutionTests
{
    [Fact]
    public void InspectedHcexe_LoadsImageOwnedStackMaps_AndRunsRealPipelineToKernelExit()
    {
        Fixture fixture = BuildFixture();
        HybridCpuIseSparseMainMemoryAreaV1 memory = new HybridCpuIseSparseMainMemoryAreaV1();
        DeterministicRuntimeKernelV1 kernel = new DeterministicRuntimeKernelV1();
        HybridCpuIseManagedImageLoadResultV1 loaded = new HybridCpuIseManagedImageLoaderV1().Load(
            Request(fixture, fixture.Image.ImageBytes), memory, kernel,
            fixture.Helpers);

        Assert.True(loaded.IsSuccess, loaded.Reason);
        Assert.Single(loaded.StackMaps!);
        HybridCpuStartupRegisterStateV1 registers = Assert.IsType<HybridCpuStartupRegisterStateV1>(fixture.Image.InitialRegisters);
        HybridCpuIseManagedGuestExecutionRequestV1 execution = new HybridCpuIseManagedGuestExecutionRequestV1(
            fixture.Image.ImageBase, fixture.Image.EntryAddress,
            HybridCpuRestrictedStartupOptionsV1.Production.ReturnSentinel,
            registers.StackPointerRegister, registers.FramePointerRegister, registers.ThreadPointerRegister,
            registers.ReturnAddressRegister, registers.ReturnValueRegister, registers.GlobalPointerRegister,
            registers.StackPointer, registers.FramePointer, registers.ThreadPointer,
            registers.ReturnAddress, registers.GlobalPointer, loaded, 0, 4096,
            RequireGcSafepointEvidence: true);

        HybridCpuIseManagedGuestExecutionResultV1 result =
            new HybridCpuIseManagedGuestExecutionRunnerV1().Execute(execution, memory, kernel);

        Assert.True(result.IsSuccess, result.Reason);
        Assert.Equal(0, result.ProcessExitCode);
        Assert.True(result.GcSafepointsObserved > 0);
        Assert.NotEmpty(result.GcResultDigests!);
        Assert.All(result.GcResultDigests!, static digest => Assert.Equal(64, digest.Length));
        Assert.Null(kernel.CurrentContext());
    }

    [Fact]
    public void LoaderRejectsDigestMismatchedImageOwnedGcMetadataBeforeExecution()
    {
        Fixture fixture = BuildFixture();
        byte[] corrupt = fixture.Image.ImageBytes.ToArray();
        corrupt[fixture.GcImageOffset + 12] ^= 0x40;
        HybridCpuIseSparseMainMemoryAreaV1 memory = new HybridCpuIseSparseMainMemoryAreaV1();

        HybridCpuIseManagedImageLoadResultV1 result = new HybridCpuIseManagedImageLoaderV1().Load(
            Request(fixture, corrupt), memory, new DeterministicRuntimeKernelV1(),
            fixture.Helpers);

        Assert.Equal(HybridCpuIseManagedImageLoadStatusV1.GcMetadataRejected, result.Status);
        Assert.Contains("metadata is absent", result.Reason, StringComparison.Ordinal);
        Assert.False(result.HasExecutionAuthority);
    }

    private static Fixture BuildFixture()
    {
        const string entry = "phase09_ise_eh_entry";
        HybridCpuManagedTypeSystemBuildV1 types = new HybridCpuManagedTypeSystemBuilderV1().Build(
            [new("Phase09.IseFixture", HybridCpuManagedTypeKindV1.Class, null, [], [])]);
        Assert.True(types.IsSuccess, types.Reason);
        HybridCpuManagedTypeDescriptorV1 type = Assert.Single(types.TypeSystem!.Descriptors);
        HybridCpuManagedTypeMetadataArtifactV1 typeMetadata = new HybridCpuManagedTypeMetadataEncoderV1().Encode([type]);
        Assert.Equal(HybridCpuManagedTypeMetadataStatusV1.Encoded, typeMetadata.Status);

        HybridCpuGcInfoEncodingResultV1 gc = HybridCpuManagedAbiEncodingV1.EncodeGcInfo(
            [new(0, HybridCpuSafepointCategoryV1.CallSite, [])]);
        Assert.Equal(HybridCpuPlatformFactStatus.Supported, gc.Status);
        byte[] unwind = HybridCpuManagedUnwindCodecV2.Encode(new(
            HybridCpuManagedFrameKindV1.Managed, HybridCpuManagedCfaBaseV1.StackPointer, 0,
            HybridCpuNativeAbiContractV2.ReturnAddressRegister, null, []));
        byte[] code = Code();
        HybridCpuObjectWriterV1 writer = new HybridCpuObjectWriterV1();
        HybridCpuObjectArtifactV1 obj = writer.Write(new(
            [
                new(".text", HybridCpuObjectSectionKind.Code, HybridCpuBundleSerializer.BundleSizeBytes,
                    code, (ulong)code.Length),
                new(".hcgc", HybridCpuObjectSectionKind.ReadOnlyData, 8, gc.Bytes, (ulong)gc.Bytes.Length),
                new(".hcunwind", HybridCpuObjectSectionKind.Unwind, 8, unwind, (ulong)unwind.Length),
                new(".hctypes", HybridCpuObjectSectionKind.ReadOnlyData, 8,
                    typeMetadata.Bytes, (ulong)typeMetadata.Bytes.Length)
            ],
            [
                new(entry, HybridCpuSymbolBinding.Global, HybridCpuSymbolVisibility.Default,
                    ".text", 0, (ulong)code.Length, true),
                new("phase09_gc", HybridCpuSymbolBinding.Global, HybridCpuSymbolVisibility.Hidden,
                    ".hcgc", 0, (ulong)gc.Bytes.Length, true),
                new("phase09_unwind", HybridCpuSymbolBinding.Global, HybridCpuSymbolVisibility.Hidden,
                    ".hcunwind", 0, (ulong)unwind.Length, true),
                new("phase09_types", HybridCpuSymbolBinding.Global, HybridCpuSymbolVisibility.Hidden,
                    ".hctypes", 0, (ulong)typeMetadata.Bytes.Length, true)
            ], [], HybridCpuTargetPlatformContractV1.Default.ContractDigest,
            HybridCpuManagedAbiFamilyV1.Default.ContractDigest));
        Assert.Equal(HybridCpuObjectStatusV1.Success, obj.Status);
        HybridCpuStaticLinkArtifactV1 link = new HybridCpuStaticLinkerV1().Link([new("phase09-ise", obj.Bytes)]);
        Assert.Equal(HybridCpuLinkStatusV1.Success, link.Status);
        HybridCpuLinkedSymbolV1 codeSymbol = Symbol(link, entry);
        HybridCpuLinkedSymbolV1 gcSymbol = Symbol(link, "phase09_gc");
        HybridCpuLinkedSymbolV1 typeSymbol = Symbol(link, "phase09_types");
        string Raw(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        HybridCpuCodeManagerRegistrationV1 codeRecord = new HybridCpuCodeManagerRegistrationV1(entry,
            checked((int)(codeSymbol.Address - link.ImageBase)), checked((int)codeSymbol.Size),
            Raw(gc.Bytes), Raw(unwind));
        HybridCpuImageRuntimeBootstrapDescriptorV1 bootstrap = HybridCpuImageRuntimeBootstrapContractV1.Create(
            HybridCpuManagedAbiFamilyV1.Default.ContractDigest, entry, entry, [], [codeRecord],
            managedTypes: [new(type.TypeId, type.StableIdentity, type.DescriptorDigest,
                checked((int)(typeSymbol.Address - link.ImageBase)), checked((int)typeSymbol.Size), null,
                TypeHandle: 1)]);
        HybridCpuRestrictedImageV1 built = new HybridCpuRestrictedImageBuilderV1().Build(
            new(link, entry, RuntimeBootstrap: bootstrap));
        Assert.Equal(HybridCpuStartupStatusV1.Success, built.Status);
        HybridCpuRestrictedImageV1 inspected = new HybridCpuRestrictedImageBuilderV1().Inspect(built.PackageBytes);
        Assert.Equal(HybridCpuStartupStatusV1.Success, inspected.Status);
        return new(inspected, checked((int)(gcSymbol.Address - link.ImageBase)),
            new Dictionary<string, HybridCpuRuntimeHelperEntryV1>());
    }

    [Fact]
    public void NativeEndFinally_UsesImageDispatchAndTransfersToBoundedTargetOnRealPipeline()
    {
        Fixture fixture = BuildFinallyFixture();
        HybridCpuIseSparseMainMemoryAreaV1 memory = new HybridCpuIseSparseMainMemoryAreaV1();
        DeterministicRuntimeKernelV1 kernel = new DeterministicRuntimeKernelV1();
        HybridCpuIseManagedImageLoadResultV1 loaded = new HybridCpuIseManagedImageLoaderV1().Load(
            Request(fixture, fixture.Image.ImageBytes), memory, kernel, fixture.Helpers);
        Assert.True(loaded.IsSuccess, loaded.Reason);
        HybridCpuStartupRegisterStateV1 registers = Assert.IsType<HybridCpuStartupRegisterStateV1>(fixture.Image.InitialRegisters);
        HybridCpuIseManagedGuestExecutionResultV1 result = new HybridCpuIseManagedGuestExecutionRunnerV1().Execute(
            new(fixture.Image.ImageBase, fixture.Image.EntryAddress,
                HybridCpuRestrictedStartupOptionsV1.Production.ReturnSentinel,
                registers.StackPointerRegister, registers.FramePointerRegister, registers.ThreadPointerRegister,
                registers.ReturnAddressRegister, registers.ReturnValueRegister, registers.GlobalPointerRegister,
                registers.StackPointer, registers.FramePointer, registers.ThreadPointer, registers.ReturnAddress,
                registers.GlobalPointer, loaded, 0, 16384, RequireGcSafepointEvidence: true), memory, kernel);
        Assert.True(result.IsSuccess, result.Reason);
        Assert.Equal(0, result.ProcessExitCode);
        Assert.True(result.GcSafepointsObserved > 0);
    }

    [Fact]
    public void NativeEndFinally_CorruptImageOwnedContinuationFailsClosedWithExit255()
    {
        Fixture fixture = BuildFinallyFixture(corruptFinallyMetadata: true);
        HybridCpuIseSparseMainMemoryAreaV1 memory = new HybridCpuIseSparseMainMemoryAreaV1();
        DeterministicRuntimeKernelV1 kernel = new DeterministicRuntimeKernelV1();
        HybridCpuIseManagedImageLoadResultV1 loaded = new HybridCpuIseManagedImageLoaderV1().Load(
            Request(fixture, fixture.Image.ImageBytes), memory, kernel, fixture.Helpers);
        Assert.True(loaded.IsSuccess, loaded.Reason);
        HybridCpuStartupRegisterStateV1 registers = Assert.IsType<HybridCpuStartupRegisterStateV1>(fixture.Image.InitialRegisters);
        HybridCpuIseManagedGuestExecutionResultV1 result = new HybridCpuIseManagedGuestExecutionRunnerV1().Execute(
            new(fixture.Image.ImageBase, fixture.Image.EntryAddress,
                HybridCpuRestrictedStartupOptionsV1.Production.ReturnSentinel,
                registers.StackPointerRegister, registers.FramePointerRegister, registers.ThreadPointerRegister,
                registers.ReturnAddressRegister, registers.ReturnValueRegister, registers.GlobalPointerRegister,
                registers.StackPointer, registers.FramePointer, registers.ThreadPointer, registers.ReturnAddress,
                registers.GlobalPointer, loaded, 0, 16384, RequireGcSafepointEvidence: true), memory, kernel);
        Assert.True(result.IsSuccess, result.Reason);
        Assert.Equal(255, result.ProcessExitCode);
        Assert.True(result.GcSafepointsObserved > 0);
    }

    [Fact]
    public void NativeThrow_UsesImageOwnedEhStateAndTransfersToExactCatchOnRealPipeline()
    {
        Fixture fixture = BuildExceptionalFixture(hasCatch: true);
        HybridCpuIseSparseMainMemoryAreaV1 memory = new HybridCpuIseSparseMainMemoryAreaV1();
        DeterministicRuntimeKernelV1 kernel = new DeterministicRuntimeKernelV1();
        HybridCpuIseManagedImageLoadResultV1 loaded = new HybridCpuIseManagedImageLoaderV1().Load(
            Request(fixture, fixture.Image.ImageBytes), memory, kernel, fixture.Helpers);
        Assert.True(loaded.IsSuccess, loaded.Reason);
        HybridCpuManagedHeapResultV1 exception = loaded.Heap!.Allocate(1);
        Assert.True(exception.IsSuccess, exception.Reason);
        HybridCpuStartupRegisterStateV1 registers = Assert.IsType<HybridCpuStartupRegisterStateV1>(
            fixture.Image.InitialRegisters);
        HybridCpuIseManagedGuestExecutionResultV1 result = new HybridCpuIseManagedGuestExecutionRunnerV1().Execute(
            new(fixture.Image.ImageBase, fixture.Image.EntryAddress,
                HybridCpuRestrictedStartupOptionsV1.Production.ReturnSentinel,
                registers.StackPointerRegister, registers.FramePointerRegister, registers.ThreadPointerRegister,
                registers.ReturnAddressRegister, registers.ReturnValueRegister, registers.GlobalPointerRegister,
                registers.StackPointer, registers.FramePointer, registers.ThreadPointer, registers.ReturnAddress,
                registers.GlobalPointer, loaded, 0, 32768, RequireGcSafepointEvidence: true,
                AdditionalRegisterSeeds: [new(11, exception.ObjectReference)]), memory, kernel);

        Assert.True(result.IsSuccess, result.Reason);
        Assert.Equal(0, result.ProcessExitCode);
        Assert.True(result.GcSafepointsObserved > 0);
        Assert.NotEmpty(result.GcResultDigests!);
        Assert.Null(kernel.CurrentContext());
    }

    [Fact]
    public void NativeThrow_WithoutCatchUnwindsAndTerminatesThroughRetiredProcessExitEcall()
    {
        Fixture fixture = BuildExceptionalFixture(hasCatch: false);
        HybridCpuIseSparseMainMemoryAreaV1 memory = new HybridCpuIseSparseMainMemoryAreaV1();
        DeterministicRuntimeKernelV1 kernel = new DeterministicRuntimeKernelV1();
        HybridCpuIseManagedImageLoadResultV1 loaded = new HybridCpuIseManagedImageLoaderV1().Load(
            Request(fixture, fixture.Image.ImageBytes), memory, kernel, fixture.Helpers);
        Assert.True(loaded.IsSuccess, loaded.Reason);
        HybridCpuManagedHeapResultV1 exception = loaded.Heap!.Allocate(1);
        Assert.True(exception.IsSuccess, exception.Reason);
        HybridCpuStartupRegisterStateV1 registers = Assert.IsType<HybridCpuStartupRegisterStateV1>(
            fixture.Image.InitialRegisters);
        HybridCpuIseManagedGuestExecutionResultV1 result = new HybridCpuIseManagedGuestExecutionRunnerV1().Execute(
            new(fixture.Image.ImageBase, fixture.Image.EntryAddress,
                HybridCpuRestrictedStartupOptionsV1.Production.ReturnSentinel,
                registers.StackPointerRegister, registers.FramePointerRegister, registers.ThreadPointerRegister,
                registers.ReturnAddressRegister, registers.ReturnValueRegister, registers.GlobalPointerRegister,
                registers.StackPointer, registers.FramePointer, registers.ThreadPointer, registers.ReturnAddress,
                registers.GlobalPointer, loaded, 0, 65536, RequireGcSafepointEvidence: true,
                AdditionalRegisterSeeds: [new(11, exception.ObjectReference)]), memory, kernel);

        Assert.True(result.IsSuccess, result.Reason);
        Assert.Equal(255, result.ProcessExitCode);
        Assert.Equal(1UL, result.ProcessExitEcallsObserved);
        Assert.True(result.GcSafepointsObserved > 0);
        Assert.Null(kernel.CurrentContext());
    }

    [Fact]
    public void NativeThrow_MalformedImageOwnedEhFailsClosedThroughRetiredProcessExitEcall()
    {
        Fixture fixture = BuildExceptionalFixture(hasCatch: true, corruptEh: true);
        HybridCpuIseSparseMainMemoryAreaV1 memory = new HybridCpuIseSparseMainMemoryAreaV1();
        DeterministicRuntimeKernelV1 kernel = new DeterministicRuntimeKernelV1();
        HybridCpuIseManagedImageLoadResultV1 loaded = new HybridCpuIseManagedImageLoaderV1().Load(
            Request(fixture, fixture.Image.ImageBytes), memory, kernel, fixture.Helpers);
        Assert.True(loaded.IsSuccess, loaded.Reason);
        HybridCpuManagedHeapResultV1 exception = loaded.Heap!.Allocate(1);
        Assert.True(exception.IsSuccess, exception.Reason);
        HybridCpuStartupRegisterStateV1 registers = Assert.IsType<HybridCpuStartupRegisterStateV1>(
            fixture.Image.InitialRegisters);
        HybridCpuIseManagedGuestExecutionResultV1 result = new HybridCpuIseManagedGuestExecutionRunnerV1().Execute(
            new(fixture.Image.ImageBase, fixture.Image.EntryAddress,
                HybridCpuRestrictedStartupOptionsV1.Production.ReturnSentinel,
                registers.StackPointerRegister, registers.FramePointerRegister, registers.ThreadPointerRegister,
                registers.ReturnAddressRegister, registers.ReturnValueRegister, registers.GlobalPointerRegister,
                registers.StackPointer, registers.FramePointer, registers.ThreadPointer, registers.ReturnAddress,
                registers.GlobalPointer, loaded, 0, 65536, RequireGcSafepointEvidence: true,
                AdditionalRegisterSeeds: [new(11, exception.ObjectReference)]), memory, kernel);

        Assert.True(result.IsSuccess, result.Reason);
        Assert.Equal(255, result.ProcessExitCode);
        Assert.Equal(1UL, result.ProcessExitEcallsObserved);
        Assert.True(result.GcSafepointsObserved > 0);
        Assert.Null(kernel.CurrentContext());
    }

    private static Fixture BuildExceptionalFixture(bool hasCatch, bool corruptEh = false)
    {
        const string entry = "phase09_native_exception_entry";
        byte[] code = ExceptionalCode();
        byte[] eh = ExceptionalEh(hasCatch);
        if (corruptEh)
            eh[HybridCpuManagedEhClauseEncodingV1.HeaderSizeBytes + 1] = 1;
        HybridCpuGcInfoEncodingResultV1 gc = HybridCpuManagedAbiEncodingV1.EncodeGcInfo([
            new(3 * HybridCpuBundleSerializer.BundleSizeBytes, HybridCpuSafepointCategoryV1.CallSite,
                [new("exception", HybridCpuGcReferenceKindV1.ObjectReference,
                    HybridCpuGcLocationKindV1.Register, 10, null)])]);
        Assert.Equal(HybridCpuPlatformFactStatus.Supported, gc.Status);
        HybridCpuManagedUnwindRecordV2 unwindRecord = new HybridCpuManagedUnwindRecordV2(HybridCpuManagedFrameKindV1.Managed,
            HybridCpuManagedCfaBaseV1.StackPointer, 0, 20, null,
            [new(20, 0)]);
        byte[] unwind = HybridCpuManagedUnwindCodecV2.Encode(unwindRecord);
        HybridCpuManagedTypeSystemBuildV1 types = new HybridCpuManagedTypeSystemBuilderV1().Build(
            [new("Phase09.ExceptionFixture", HybridCpuManagedTypeKindV1.Class, null, [], [])]);
        Assert.True(types.IsSuccess, types.Reason);
        HybridCpuManagedTypeDescriptorV1 type = Assert.Single(types.TypeSystem!.Descriptors);
        HybridCpuManagedTypeMetadataArtifactV1 typeMetadata = new HybridCpuManagedTypeMetadataEncoderV1().Encode([type]);
        string GcSymbol() => ScalarControlFlowV2ObjectLinkerV1.MetadataSymbol(entry, "gc");
        string UnwindSymbol() => ScalarControlFlowV2ObjectLinkerV1.MetadataSymbol(entry, "unwind");
        string EhSymbol() => ScalarControlFlowV2ObjectLinkerV1.MetadataSymbol(entry, "eh");
        HybridCpuObjectWriterV1 writer = new HybridCpuObjectWriterV1();
        HybridCpuObjectArtifactV1 method = writer.Write(new(
            [
                new(".text", HybridCpuObjectSectionKind.Code, HybridCpuBundleSerializer.BundleSizeBytes, code, (ulong)code.Length),
                new(".hcgc", HybridCpuObjectSectionKind.ReadOnlyData, 8, gc.Bytes, (ulong)gc.Bytes.Length),
                new(".hcunwind", HybridCpuObjectSectionKind.Unwind, 8, unwind, (ulong)unwind.Length),
                new(".hceh", HybridCpuObjectSectionKind.ReadOnlyData, 8, eh, (ulong)eh.Length),
                new(".hctypes", HybridCpuObjectSectionKind.ReadOnlyData, 8, typeMetadata.Bytes, (ulong)typeMetadata.Bytes.Length)
            ],
            [
                new(entry, HybridCpuSymbolBinding.Global, HybridCpuSymbolVisibility.Default, ".text", 0, (ulong)code.Length, true),
                new(HybridCpuManagedEhCatchDispatchEmitterV1.ThrowSymbol, HybridCpuSymbolBinding.Global,
                    HybridCpuSymbolVisibility.Hidden, null, 0, 0, false),
                new(GcSymbol(), HybridCpuSymbolBinding.Global, HybridCpuSymbolVisibility.Hidden, ".hcgc", 0, (ulong)gc.Bytes.Length, true),
                new(UnwindSymbol(), HybridCpuSymbolBinding.Global, HybridCpuSymbolVisibility.Hidden, ".hcunwind", 0, (ulong)unwind.Length, true),
                new(EhSymbol(), HybridCpuSymbolBinding.Global, HybridCpuSymbolVisibility.Hidden, ".hceh", 0, (ulong)eh.Length, true),
                new("phase09_exception_types", HybridCpuSymbolBinding.Global, HybridCpuSymbolVisibility.Hidden,
                    ".hctypes", 0, (ulong)typeMetadata.Bytes.Length, true)
            ],
            [new(".text", 3UL * HybridCpuBundleSerializer.BundleSizeBytes,
                HybridCpuRelocationKind.ManagedCallRelativeSigned16,
                HybridCpuManagedEhCatchDispatchEmitterV1.ThrowSymbol, 0)],
            HybridCpuTargetPlatformContractV1.Default.ContractDigest,
            HybridCpuManagedAbiFamilyV1.Default.ContractDigest));
        HybridCpuObjectArtifactV1 dispatch = ManagedEhDispatchTableObjectV1.Emit(
            [new(entry, code.Length, gc.Bytes.Length, unwind.Length, eh.Length, 0, unwindRecord)],
            [new(1, type.TypeId, 0)], HybridCpuRestrictedStartupOptionsV1.Production.StackBase,
            HybridCpuRestrictedStartupOptionsV1.Production.StackSize, bindRuntimeHelpers: true);
        HybridCpuStaticLinkArtifactV1 link = new HybridCpuStaticLinkerV1().Link([
            new("phase09-exception-method", method.Bytes),
            new(HybridCpuManagedEhCatchDispatchEmitterV1.ModuleIdentity, HybridCpuManagedEhCatchDispatchEmitterV1.EmitObject().Bytes),
            new(HybridCpuManagedEhFrameLookupEmitterV1.ModuleIdentity, HybridCpuManagedEhFrameLookupEmitterV1.EmitObject().Bytes),
            new(HybridCpuManagedEhCatchSelectorEmitterV1.ModuleIdentity, HybridCpuManagedEhCatchSelectorEmitterV1.EmitObject().Bytes),
            new(HybridCpuManagedEhClauseSelectorEmitterV1.ModuleIdentity, HybridCpuManagedEhClauseSelectorEmitterV1.EmitObject().Bytes),
            new(HybridCpuManagedEhUnwindStepEmitterV1.ModuleIdentity, HybridCpuManagedEhUnwindStepEmitterV1.EmitObject().Bytes),
            new(HybridCpuManagedExceptionTransferObjectV1.ModuleIdentity, HybridCpuManagedExceptionTransferObjectV1.Emit().Bytes),
            new(HybridCpuManagedProcessExitEmitterV1.ModuleIdentity, HybridCpuManagedProcessExitEmitterV1.EmitObject().Bytes),
            new(HybridCpuManagedEhStateObjectV1.ModuleIdentity, HybridCpuManagedEhStateObjectV1.Emit().Bytes),
            new(HybridCpuManagedEhScopeEmitterV1.RethrowModuleIdentity, HybridCpuManagedEhScopeEmitterV1.EmitRethrowObject().Bytes),
            new(HybridCpuManagedEhScopeEmitterV1.LeaveModuleIdentity, HybridCpuManagedEhScopeEmitterV1.EmitLeaveCatchObject().Bytes),
            new(HybridCpuManagedEhFinallySelectorEmitterV1.ModuleIdentity, HybridCpuManagedEhFinallySelectorEmitterV1.EmitObject().Bytes),
            new(HybridCpuManagedEhExceptionalResumeEmitterV1.ModuleIdentity, HybridCpuManagedEhExceptionalResumeEmitterV1.EmitObject().Bytes),
            new(ManagedEhDispatchTableObjectV1.ModuleIdentity, dispatch.Bytes)]);
        Assert.Equal(HybridCpuLinkStatusV1.Success, link.Status);
        HybridCpuLinkedSymbolV1 codeSymbol = Symbol(link, entry);
        HybridCpuLinkedSymbolV1 gcSymbol = Symbol(link, GcSymbol());
        HybridCpuLinkedSymbolV1 typeSymbol = Symbol(link, "phase09_exception_types");
        string Raw(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        HybridCpuCodeManagerRegistrationV1 record = new HybridCpuCodeManagerRegistrationV1(entry,
            checked((int)(codeSymbol.Address - link.ImageBase)), code.Length, Raw(gc.Bytes), Raw(unwind));
        string[] helperNames = link.Symbols.Select(static row => row.Name)
            .Where(name => HybridCpuManagedAbiFamilyV1.Default.ResolveRuntimeHelper(name) is not null)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        HybridCpuRuntimeHelperImportV1[] imports = helperNames.Select(name =>
        {
            HybridCpuRuntimeHelperV1 helper = HybridCpuManagedAbiFamilyV1.Default.ResolveRuntimeHelper(name)!;
            return new HybridCpuRuntimeHelperImportV1(helper.Symbol, helper.Signature, true);
        }).ToArray();
        HybridCpuImageRuntimeBootstrapDescriptorV1 bootstrap = HybridCpuImageRuntimeBootstrapContractV1.Create(
            HybridCpuManagedAbiFamilyV1.Default.ContractDigest, entry, entry, imports, [record],
            managedTypes: [new(type.TypeId, type.StableIdentity, type.DescriptorDigest,
                checked((int)(typeSymbol.Address - link.ImageBase)), checked((int)typeSymbol.Size), null,
                TypeHandle: 1)]);
        HybridCpuRestrictedImageV1 built = new HybridCpuRestrictedImageBuilderV1().Build(new(link, entry,
            GlobalPointerSymbol: ManagedEhDispatchTableObjectV1.Symbol, RuntimeBootstrap: bootstrap));
        Assert.Equal(HybridCpuStartupStatusV1.Success, built.Status);
        HybridCpuRestrictedImageV1 inspected = new HybridCpuRestrictedImageBuilderV1().Inspect(built.PackageBytes);
        Assert.Equal(HybridCpuStartupStatusV1.Success, inspected.Status);
        return new(inspected, checked((int)(gcSymbol.Address - link.ImageBase)), helperNames.ToDictionary(
            static name => name, static _ => (HybridCpuRuntimeHelperEntryV1)(_ => true), StringComparer.Ordinal));
    }

    private static byte[] ExceptionalCode()
    {
        HybridCpuInstructionWord Word(HybridCpuOpcode opcode, byte rd, byte rs1,
            byte rs2 = HybridCpuInstructionWord.NoArchReg, short immediate = 0) => new()
        {
            OpCode = (uint)opcode, DataTypeValue = HybridCpuDataType.INT64, PredicateMask = byte.MaxValue,
            Word1 = HybridCpuInstructionWord.PackArchRegs(rd, rs1, rs2), Immediate = unchecked((ushort)immediate)
        };
        HybridCpuInstructionBundle Bundle(HybridCpuInstructionWord word)
        { HybridCpuInstructionBundle bundle = new HybridCpuInstructionBundle(); bundle.SetInstruction(0, word); return bundle; }
        return new HybridCpuBundleSerializer().SerializeProgram([
            Bundle(Word(HybridCpuOpcode.ADDI, 20, 1)),
            Bundle(Word(HybridCpuOpcode.SD, HybridCpuInstructionWord.NoArchReg, 2, 20)),
            Bundle(Word(HybridCpuOpcode.ADDI, 10, 11)),
            Bundle(Word(HybridCpuOpcode.JAL, 1, HybridCpuInstructionWord.NoArchReg)),
            Bundle(Word(HybridCpuOpcode.ADDI, 10, 0)),
            Bundle(Word(HybridCpuOpcode.JALR, 0, 20))]);
    }

    private static byte[] ExceptionalEh(bool hasCatch)
    {
        byte[] bytes = new byte[HybridCpuManagedEhClauseEncodingV1.HeaderSizeBytes +
            (hasCatch ? HybridCpuManagedEhClauseEncodingV1.ClauseSizeBytes : 0)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, HybridCpuManagedEhSchemaV1.EhMagic);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), HybridCpuManagedEhSchemaV1.SchemaVersion);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(HybridCpuManagedEhClauseEncodingV1.CountOffset), hasCatch ? 1 : 0);
        if (!hasCatch) return bytes;
        int clause = HybridCpuManagedEhClauseEncodingV1.HeaderSizeBytes;
        bytes[clause] = (byte)HybridCpuManagedEhClauseKindV1.Catch;
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(clause + HybridCpuManagedEhClauseEncodingV1.TryStartOffset), 0);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(clause + HybridCpuManagedEhClauseEncodingV1.TrySizeOffset), 4 * 256);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(clause + HybridCpuManagedEhClauseEncodingV1.HandlerStartOffset), 4 * 256);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(clause + HybridCpuManagedEhClauseEncodingV1.HandlerSizeOffset), 2 * 256);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(clause + HybridCpuManagedEhClauseEncodingV1.CatchTypeIdOffset),
            new HybridCpuManagedTypeSystemBuilderV1().Build([new("Phase09.ExceptionFixture", HybridCpuManagedTypeKindV1.Class, null, [], [])])
                .TypeSystem!.Descriptors.Single().TypeId);
        return bytes;
    }

    private static Fixture BuildFinallyFixture(bool corruptFinallyMetadata = false)
    {
        const string entry = "phase09_native_endfinally_entry";
        byte[] code = FinallyCode();
        byte[] finallyInfo = FinallyMetadata();
        if (corruptFinallyMetadata) finallyInfo[0] ^= 0x01;
        HybridCpuGcInfoEncodingResultV1 gc = HybridCpuManagedAbiEncodingV1.EncodeGcInfo(
            [new(3 * HybridCpuBundleSerializer.BundleSizeBytes, HybridCpuSafepointCategoryV1.CallSite, [])]);
        HybridCpuManagedUnwindRecordV2 unwindRecord = new HybridCpuManagedUnwindRecordV2(HybridCpuManagedFrameKindV1.Managed,
            HybridCpuManagedCfaBaseV1.StackPointer, 0, 20, null, []);
        byte[] unwind = HybridCpuManagedUnwindCodecV2.Encode(unwindRecord);
        HybridCpuManagedTypeSystemBuildV1 types = new HybridCpuManagedTypeSystemBuilderV1().Build(
            [new("Phase09.FinallyFixture", HybridCpuManagedTypeKindV1.Class, null, [], [])]);
        Assert.True(types.IsSuccess, types.Reason);
        HybridCpuManagedTypeDescriptorV1 type = Assert.Single(types.TypeSystem!.Descriptors);
        HybridCpuManagedTypeMetadataArtifactV1 typeMetadata = new HybridCpuManagedTypeMetadataEncoderV1().Encode([type]);
        string GcSymbol() => ScalarControlFlowV2ObjectLinkerV1.MetadataSymbol(entry, "gc");
        string UnwindSymbol() => ScalarControlFlowV2ObjectLinkerV1.MetadataSymbol(entry, "unwind");
        string FinallySymbol() => ScalarControlFlowV2ObjectLinkerV1.MetadataSymbol(entry, "finally");
        HybridCpuObjectWriterV1 writer = new HybridCpuObjectWriterV1();
        HybridCpuObjectArtifactV1 method = writer.Write(new(
            [
                new(".text", HybridCpuObjectSectionKind.Code, HybridCpuBundleSerializer.BundleSizeBytes, code, (ulong)code.Length),
                new(".hcgc", HybridCpuObjectSectionKind.ReadOnlyData, 8, gc.Bytes, (ulong)gc.Bytes.Length),
                new(".hcunwind", HybridCpuObjectSectionKind.Unwind, 8, unwind, (ulong)unwind.Length),
                new(".hcfinally", HybridCpuObjectSectionKind.ReadOnlyData, 8, finallyInfo, (ulong)finallyInfo.Length),
                new(".hctypes", HybridCpuObjectSectionKind.ReadOnlyData, 8, typeMetadata.Bytes, (ulong)typeMetadata.Bytes.Length)
            ],
            [
                new(entry, HybridCpuSymbolBinding.Global, HybridCpuSymbolVisibility.Default, ".text", 0, (ulong)code.Length, true),
                new(HybridCpuManagedFinallyContinuationEmitterV1.Symbol, HybridCpuSymbolBinding.Global,
                    HybridCpuSymbolVisibility.Hidden, null, 0, 0, false),
                new(GcSymbol(), HybridCpuSymbolBinding.Global, HybridCpuSymbolVisibility.Hidden, ".hcgc", 0, (ulong)gc.Bytes.Length, true),
                new(UnwindSymbol(), HybridCpuSymbolBinding.Global, HybridCpuSymbolVisibility.Hidden, ".hcunwind", 0, (ulong)unwind.Length, true),
                new(FinallySymbol(), HybridCpuSymbolBinding.Global, HybridCpuSymbolVisibility.Hidden, ".hcfinally", 0, (ulong)finallyInfo.Length, true),
                new("phase09_finally_types", HybridCpuSymbolBinding.Global, HybridCpuSymbolVisibility.Hidden,
                    ".hctypes", 0, (ulong)typeMetadata.Bytes.Length, true)
            ],
            [new(".text", 3UL * HybridCpuBundleSerializer.BundleSizeBytes,
                HybridCpuRelocationKind.ManagedCallRelativeSigned16,
                HybridCpuManagedFinallyContinuationEmitterV1.Symbol, 0)],
            HybridCpuTargetPlatformContractV1.Default.ContractDigest,
            HybridCpuManagedAbiFamilyV1.Default.ContractDigest));
        HybridCpuObjectArtifactV1 dispatch = ManagedEhDispatchTableObjectV1.Emit(
            [new(entry, code.Length, gc.Bytes.Length, unwind.Length, 0, finallyInfo.Length, unwindRecord)], [],
            HybridCpuRestrictedStartupOptionsV1.Production.StackBase,
            HybridCpuRestrictedStartupOptionsV1.Production.StackSize,
            false, false, false, false, true);
        HybridCpuStaticLinkArtifactV1 link = new HybridCpuStaticLinkerV1().Link([
            new("phase09-method", method.Bytes),
            new(HybridCpuManagedFinallyContinuationEmitterV1.ModuleIdentity,
                HybridCpuManagedFinallyContinuationEmitterV1.EmitObject().Bytes),
            new(HybridCpuManagedEhFrameLookupEmitterV1.ModuleIdentity,
                HybridCpuManagedEhFrameLookupEmitterV1.EmitObject().Bytes),
            new(HybridCpuManagedProcessExitEmitterV1.ModuleIdentity,
                HybridCpuManagedProcessExitEmitterV1.EmitObject().Bytes),
            new(ManagedEhDispatchTableObjectV1.ModuleIdentity, dispatch.Bytes)]);
        Assert.Equal(HybridCpuLinkStatusV1.Success, link.Status);
        HybridCpuLinkedSymbolV1 codeSymbol = Symbol(link, entry);
        HybridCpuLinkedSymbolV1 gcSymbol = Symbol(link, GcSymbol());
        HybridCpuLinkedSymbolV1 typeSymbol = Symbol(link, "phase09_finally_types");
        string Raw(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        HybridCpuCodeManagerRegistrationV1 record = new HybridCpuCodeManagerRegistrationV1(entry,
            checked((int)(codeSymbol.Address - link.ImageBase)), code.Length, Raw(gc.Bytes), Raw(unwind));
        string[] helperNames = link.Symbols.Select(static row => row.Name)
            .Where(name => HybridCpuManagedAbiFamilyV1.Default.ResolveRuntimeHelper(name) is not null)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        HybridCpuRuntimeHelperImportV1[] imports = helperNames.Select(name =>
        {
            HybridCpuRuntimeHelperV1 helper = HybridCpuManagedAbiFamilyV1.Default.ResolveRuntimeHelper(name)!;
            return new HybridCpuRuntimeHelperImportV1(helper.Symbol, helper.Signature, true);
        }).ToArray();
        HybridCpuImageRuntimeBootstrapDescriptorV1 bootstrap = HybridCpuImageRuntimeBootstrapContractV1.Create(
            HybridCpuManagedAbiFamilyV1.Default.ContractDigest, entry, entry, imports, [record],
            managedTypes: [new(type.TypeId, type.StableIdentity, type.DescriptorDigest,
                checked((int)(typeSymbol.Address - link.ImageBase)), checked((int)typeSymbol.Size), null,
                TypeHandle: 1)]);
        HybridCpuRestrictedImageV1 built = new HybridCpuRestrictedImageBuilderV1().Build(new(link, entry,
            GlobalPointerSymbol: ManagedEhDispatchTableObjectV1.Symbol, RuntimeBootstrap: bootstrap));
        Assert.Equal(HybridCpuStartupStatusV1.Success, built.Status);
        HybridCpuRestrictedImageV1 inspected = new HybridCpuRestrictedImageBuilderV1().Inspect(built.PackageBytes);
        Assert.Equal(HybridCpuStartupStatusV1.Success, inspected.Status);
        return new(inspected, checked((int)(gcSymbol.Address - link.ImageBase)), helperNames.ToDictionary(
            static name => name, static _ => (HybridCpuRuntimeHelperEntryV1)(_ => true), StringComparer.Ordinal));
    }

    private static byte[] FinallyCode()
    {
        HybridCpuInstructionWord Word(HybridCpuOpcode opcode, byte rd, byte rs1,
            byte rs2 = HybridCpuInstructionWord.NoArchReg, short immediate = 0) => new()
        {
            OpCode = (uint)opcode, DataTypeValue = HybridCpuDataType.INT64, PredicateMask = byte.MaxValue,
            Word1 = HybridCpuInstructionWord.PackArchRegs(rd, rs1, rs2), Immediate = unchecked((ushort)immediate)
        };
        HybridCpuInstructionBundle Bundle(HybridCpuInstructionWord word)
        { HybridCpuInstructionBundle bundle = new HybridCpuInstructionBundle(); bundle.SetInstruction(0, word); return bundle; }
        return new HybridCpuBundleSerializer().SerializeProgram([
            Bundle(Word(HybridCpuOpcode.ADDI, 20, 1)),
            Bundle(Word(HybridCpuOpcode.ADDI, 10, 0, immediate: 7)),
            Bundle(Word(HybridCpuOpcode.SD, HybridCpuInstructionWord.NoArchReg, 2, 10, 64)),
            Bundle(Word(HybridCpuOpcode.JAL, 1, HybridCpuInstructionWord.NoArchReg)),
            Bundle(Word(HybridCpuOpcode.ADDI, 10, 0)),
            Bundle(Word(HybridCpuOpcode.JALR, 0, 20))]);
    }

    private static byte[] FinallyMetadata()
    {
        byte[] bytes = new byte[HybridCpuManagedFinallyContinuationEncodingV1.HeaderSizeBytes +
            HybridCpuManagedFinallyContinuationEncodingV1.ContinuationRowSizeBytes +
            HybridCpuManagedFinallyContinuationEncodingV1.StepRowSizeBytes];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, HybridCpuManagedFinallyContinuationEncodingV1.Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), HybridCpuManagedFinallyContinuationEncodingV1.Version);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(HybridCpuManagedFinallyContinuationEncodingV1.ContinuationCountOffset), 1);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(HybridCpuManagedFinallyContinuationEncodingV1.StepCountOffset), 1);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(HybridCpuManagedFinallyContinuationEncodingV1.TokenSlotOffset), 64);
        int continuation = HybridCpuManagedFinallyContinuationEncodingV1.HeaderSizeBytes;
        Write(continuation + HybridCpuManagedFinallyContinuationEncodingV1.TokenOffset, 7);
        Write(continuation + HybridCpuManagedFinallyContinuationEncodingV1.LeaveOffset, 3 * 256);
        Write(continuation + HybridCpuManagedFinallyContinuationEncodingV1.TargetOffset, 4 * 256);
        Write(continuation + HybridCpuManagedFinallyContinuationEncodingV1.FirstStepOffset, 0);
        Write(continuation + HybridCpuManagedFinallyContinuationEncodingV1.ContinuationStepCountOffset, 1);
        Write(continuation + HybridCpuManagedFinallyContinuationEncodingV1.ReleaseBeforeTargetOffset, 0);
        int step = continuation + HybridCpuManagedFinallyContinuationEncodingV1.ContinuationRowSizeBytes;
        Write(step + HybridCpuManagedFinallyContinuationEncodingV1.StepClauseOrdinalOffset, 0);
        Write(step + HybridCpuManagedFinallyContinuationEncodingV1.StepHandlerOffset, 3 * 256);
        Write(step + HybridCpuManagedFinallyContinuationEncodingV1.StepReleaseBeforeEntryOffset, 0);
        Write(step + HybridCpuManagedFinallyContinuationEncodingV1.StepNextClauseOrdinalOffset, -1);
        Write(step + HybridCpuManagedFinallyContinuationEncodingV1.StepNextOffset, 4 * 256);
        Write(step + HybridCpuManagedFinallyContinuationEncodingV1.StepHandlerEndOffset, 4 * 256);
        return bytes;
        void Write(int offset, int value) => BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset), value);
    }

    private static HybridCpuIseManagedImageLoadRequestV1 Request(Fixture fixture, byte[] imageBytes) =>
        new(imageBytes, fixture.Image.ImageBase, fixture.Image.EntryAddress, fixture.Image.PackageSha256,
            fixture.Image.RuntimeBootstrap!,
            HybridCpuManagedHeapOptionsV1.Create(0x2800_0000, 0x0010_0000, 4096, -3),
            HybridCpuManagedAbiFamilyV1.Default.TargetContractDigest,
            HybridCpuManagedAbiFamilyV1.Default.NativeAbiDigest,
            HybridCpuManagedAbiFamilyV1.RuntimePackRevision);

    private static HybridCpuLinkedSymbolV1 Symbol(HybridCpuStaticLinkArtifactV1 link, string name) =>
        Assert.Single(link.Symbols, row => row.Name == name);

    private static byte[] Code()
    {
        HybridCpuInstructionWord Word(HybridCpuOpcode opcode, byte destination, byte source) => new()
        {
            OpCode = (uint)opcode,
            DataTypeValue = HybridCpuDataType.INT64,
            PredicateMask = byte.MaxValue,
            Word1 = HybridCpuInstructionWord.PackArchRegs(destination, source, HybridCpuInstructionWord.NoArchReg),
            VirtualThreadId = 0
        };
        HybridCpuInstructionBundle first = new HybridCpuInstructionBundle();
        first.SetInstruction(0, Word(HybridCpuOpcode.ADDI, 10, 0));
        HybridCpuInstructionBundle second = new HybridCpuInstructionBundle();
        second.SetInstruction(0, Word(HybridCpuOpcode.JALR, 0,
            (byte)HybridCpuNativeAbiContractV2.ReturnAddressRegister));
        return new HybridCpuBundleSerializer().SerializeProgram([first, second]);
    }

    private sealed record Fixture(HybridCpuRestrictedImageV1 Image, int GcImageOffset,
        IReadOnlyDictionary<string, HybridCpuRuntimeHelperEntryV1> Helpers);
}
