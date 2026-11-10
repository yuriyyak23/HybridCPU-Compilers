using HybridCPU_ISE.CloseToHSL.Core.Runtime.Managed;
using System.Buffers.Binary;
using System.Security.Cryptography;
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
using YAKSys_Hybrid_CPU;
using YAKSys_Hybrid_CPU.Memory;

namespace HybridCPU_ISE.Tests.CompilerTests;

public sealed class CompilerRefPlan7Phase09ManagedEhTests
{
    private static readonly byte[] Corpus = File.ReadAllBytes(typeof(Phase09.EhCorpus).Assembly.Location);
    private const string CorpusType = "Phase09.EhCorpus";

    [Fact]
    public void CheckedInCil_AdmitsNestedCatchFinallyThrowAndRethrowDeterministically()
    {
        RestrictedCilImporterV1 importer = new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2);
        ManagedEhImportResultV1 first = importer.ImportManagedEhPlan(Corpus,
            new(CorpusType, nameof(Phase09.EhCorpus.NestedCatchFinally)));
        ManagedEhImportResultV1 second = importer.ImportManagedEhPlan(Corpus,
            new(CorpusType, nameof(Phase09.EhCorpus.NestedCatchFinally)));
        Assert.Equal(ManagedEhImportStatusV1.Success, first.Status);
        Assert.Equal(first.Plan!.MethodIdentity, second.Plan!.MethodIdentity);
        Assert.Equal(first.Plan.ContractDigest, second.Plan.ContractDigest);
        Assert.Equal(first.Plan.Clauses, second.Plan.Clauses);
        Assert.Equal(first.Plan.Operations, second.Plan.Operations);
        Assert.Contains(first.Plan.Clauses, static row => row.Kind == HybridCpuManagedEhClauseKindV1.Catch &&
            row.CatchTypeIdentity == "System.ArgumentException" && row.CatchTypeId != 0);
        Assert.Contains(first.Plan.Clauses, static row => row.Kind == HybridCpuManagedEhClauseKindV1.Finally);
        Assert.Contains(first.Plan.Operations, static row => row.Kind == ManagedEhOperationKindV1.Throw &&
            row.RuntimeHelperSymbol == "__hybridcpu_managed_throw");

        ManagedEhImportResultV1 rethrow = importer.ImportManagedEhPlan(Corpus,
            new(CorpusType, nameof(Phase09.EhCorpus.Rethrow)));
        Assert.Equal(ManagedEhImportStatusV1.Success, rethrow.Status);
        Assert.Contains(rethrow.Plan!.Operations, static row => row.Kind == ManagedEhOperationKindV1.Rethrow);
    }

    [Fact]
    public void FiltersFailClosedAndOrdinaryImporterDoesNotSilentlyClaimGeneralEh()
    {
        RestrictedCilImporterV1 importer = new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2);
        ManagedEhImportResultV1 filter = importer.ImportManagedEhPlan(Corpus,
            new(CorpusType, nameof(Phase09.EhCorpus.FilterIsUnsupported)));
        Assert.Equal(ManagedEhImportStatusV1.Unsupported, filter.Status);
        Assert.Equal("HCCIL1803", filter.Code);
        RestrictedCilImportResultV1 ordinary = importer.ImportImage(Corpus,
            new(CorpusType, nameof(Phase09.EhCorpus.NestedCatchFinally)), "phase09-corpus");
        Assert.Equal(RestrictedCilImportStatusV1.Unsupported, ordinary.Status);
        Assert.Equal("HCCIL1011", Assert.Single(ordinary.Diagnostics).Code);
        Assert.Contains(RestrictedCilSupportMatrixV1.Default.Features, static row =>
            row.Feature == "managed-eh-plan" && row.Support == RestrictedCilMatrixSupportV1.Supported);
    }

    [Fact]
    public void FinalizerUsesOnlyPostRaFrameAndFinalPlacementAndEmitsCanonicalHcoSections()
    {
        ManagedEhMethodPlanV1 plan = Plan();
        IrRegisterAllocationResultV1 allocation = Allocation(plan.InstructionIdentityPrefix);
        ManagedEhMetadataFinalizerV1 finalizer = new ManagedEhMetadataFinalizerV1();
        ManagedEhFinalizationArtifactV1 first = finalizer.Finalize(plan, allocation);
        ManagedEhFinalizationArtifactV1 second = finalizer.Finalize(plan, allocation);
        Assert.True(first.Status == HybridCpuManagedMetadataStatusV1.Finalized,
            $"{first.Status}: {first.Reason}");
        Assert.Equal(first.ResultDigest, second.ResultDigest);
        Assert.Equal(first.EhInfo, second.EhInfo);
        Assert.Equal(first.UnwindInfo, second.UnwindInfo);
        Assert.All(first.Clauses, static row =>
        {
            Assert.True(row.TrySizeBytes > 0);
            Assert.True(row.HandlerSizeBytes > 0);
            Assert.Equal(0, row.TryStartOffsetBytes % HybridCpuBundleSerializer.BundleSizeBytes);
        });
        Assert.Equal(HybridCpuManagedCfaBaseV1.StackPointer, first.Unwind!.CfaBase);
        Assert.Equal(allocation.Witness!.Frame.FrameSizeBytes, first.Unwind.CfaOffsetBytes);
        Assert.Equal([HybridCpuObjectSectionKind.ExceptionHandling, HybridCpuObjectSectionKind.Unwind,
                HybridCpuObjectSectionKind.ReadOnlyData],
            first.ObjectSections.Select(static row => row.Kind));

        byte[] code = ExecutableCode(allocation);
        HybridCpuObjectArtifactV1 obj = Object(plan.MethodIdentity, code, first.ObjectSections);
        Assert.Equal(HybridCpuObjectStatusV1.Success, obj.Status);
        HybridCpuObjectArtifactV1 inspected = new HybridCpuObjectWriterV1().Inspect(obj.Bytes);
        Assert.Equal(HybridCpuObjectStatusV1.Success, inspected.Status);
        Assert.Equal(obj.ObjectSha256, inspected.ObjectSha256);

        IrRegisterAllocationResultV1 stale = allocation with { Status = IrRegisterAllocationStatusV1.Unknown };
        Assert.Equal(HybridCpuManagedMetadataStatusV1.Unknown, finalizer.Finalize(plan, stale).Status);
        Assert.Equal(HybridCpuManagedMetadataStatusV1.Unknown,
            finalizer.Finalize(plan with { ContractDigest = new string('0', 64) }, allocation).Status);
    }

    [Fact]
    public void RuntimeUnwindsMultipleFramesRunsFinallyAndMatchesCatchByAssignability()
    {
        HybridCpuManagedTypeSystemV1 types = Types();
        ulong argument = Type(types, "System.ArgumentException").TypeId;
        ulong exception = Type(types, "System.Exception").TypeId;
        HybridCpuManagedEhMethodRegistrationV1 leaf = Registration("leaf", 1000, 1000,
            [new(HybridCpuManagedEhClauseKindV1.Finally, 0, 500, 500, 100, 0, 0)]);
        HybridCpuManagedEhMethodRegistrationV1 middle = Registration("middle", 2000, 1000, []);
        HybridCpuManagedEhMethodRegistrationV1 outer = Registration("outer", 3000, 1000,
            [new(HybridCpuManagedEhClauseKindV1.Catch, 0, 500, 500, 100, exception, 0)]);
        HybridCpuManagedExceptionRuntimeV1 runtime = new HybridCpuManagedExceptionRuntimeV1(types, [outer, leaf, middle]);
        List<string> finalies = new List<string>();
        HybridCpuManagedExceptionDispatchResultV1 result = runtime.Dispatch(0x4000, argument,
            [Frame("leaf", 1010, returnPc: 2010), Frame("middle", 2010, returnPc: 3010), Frame("outer", 3010)],
            (method, _, _) => { finalies.Add(method); return new(true); });
        Assert.Equal(HybridCpuManagedExceptionStatusV1.Handled, result.Status);
        Assert.Equal("outer", result.HandlerMethodIdentity);
        Assert.Equal(3500, result.HandlerInstructionPointer);
        Assert.Equal(2, result.UnwoundFrames);
        Assert.Equal(["leaf"], finalies);
        Assert.False(result.UsedArchitecturalTrap);
    }

    [Fact]
    public void CompilerProducedFinalPcMetadataUnwindsRecursiveCompiledFramesWithoutTrapAuthority()
    {
        HybridCpuManagedTypeSystemV1 types = Types();
        ulong exceptionType = Type(types, "System.Exception").TypeId;
        ManagedEhMethodPlanV1 plan = Plan(exceptionType);
        IrRegisterAllocationResultV1 allocation = Allocation(plan.InstructionIdentityPrefix);
        ManagedEhFinalizationArtifactV1 finalized = new ManagedEhMetadataFinalizerV1().Finalize(plan, allocation);
        Assert.Equal(HybridCpuManagedMetadataStatusV1.Finalized, finalized.Status);
        const int codeStart = 0x4000;
        int codeSize = allocation.FinalBundles!.BlockResults.Sum(static row => row.Bundles.Count) *
            HybridCpuBundleSerializer.BundleSizeBytes;
        HybridCpuManagedEhMethodRegistrationV1 registration = new HybridCpuManagedEhMethodRegistrationV1(plan.MethodIdentity, codeStart, codeSize,
            finalized.EhInfo, finalized.UnwindInfo);
        HybridCpuManagedExceptionRuntimeV1 runtime = new HybridCpuManagedExceptionRuntimeV1(types, [registration]);
        HybridCpuManagedEhClauseRegistrationV1 catchClause = Assert.Single(finalized.Clauses,
            static row => row.Kind == HybridCpuManagedEhClauseKindV1.Catch);
        int outerPc = checked(codeStart + catchClause.TryStartOffsetBytes + catchClause.TrySizeBytes - 1);
        int leafPc = checked(codeStart + codeSize - 1);
        ulong leafStack = 0x8000;
        ulong outerStack = checked(leafStack + (ulong)finalized.Unwind!.CfaOffsetBytes);
        HybridCpuManagedExceptionDispatchResultV1 result = runtime.Dispatch(0x9000, exceptionType,
            [Frame(plan.MethodIdentity, leafPc, leafStack, (ulong)outerPc),
                Frame(plan.MethodIdentity, outerPc, outerStack)]);
        Assert.True(result.Status == HybridCpuManagedExceptionStatusV1.Handled,
            $"{result.Status}: {string.Join(",", result.Trace)}");
        Assert.Equal(1, result.UnwoundFrames);
        Assert.Equal(plan.MethodIdentity, result.HandlerMethodIdentity);
        Assert.Equal(outerStack, result.HandlerStackPointer);
        Assert.False(result.UsedArchitecturalTrap);
        Assert.False(result.HasIseAuthority);
    }

    [Fact]
    public void RethrowSkipsCurrentCatchAndThrowInFinallyReplacesException()
    {
        HybridCpuManagedTypeSystemV1 types = Types();
        ulong exception = Type(types, "System.Exception").TypeId;
        ulong replacement = Type(types, "Phase09.ReplacementException").TypeId;
        HybridCpuManagedEhClauseRegistrationV1 catchAll = new(HybridCpuManagedEhClauseKindV1.Catch,
            0, 500, 500, 100, exception, 0);
        HybridCpuManagedEhMethodRegistrationV1 inner = Registration("inner", 1000, 1000, [catchAll]);
        HybridCpuManagedEhMethodRegistrationV1 outer = Registration("outer", 2000, 1000, [catchAll]);
        HybridCpuManagedExceptionRuntimeV1 runtime = new HybridCpuManagedExceptionRuntimeV1(types, [inner, outer]);
        HybridCpuManagedExceptionDispatchResultV1 rethrow = runtime.Dispatch(0x5000, exception,
            [Frame("inner", 1510, returnPc: 2010), Frame("outer", 2010)], rethrow: true);
        Assert.Equal("outer", rethrow.HandlerMethodIdentity);

        HybridCpuManagedEhMethodRegistrationV1 finalizer = Registration("finally", 3000, 1000,
            [new(HybridCpuManagedEhClauseKindV1.Finally, 0, 500, 500, 100, 0, 0)]);
        runtime = new(types, [finalizer, outer]);
        HybridCpuManagedExceptionDispatchResultV1 replaced = runtime.Dispatch(0x5000, exception,
            [Frame("finally", 3010, returnPc: 2010), Frame("outer", 2010)],
            (_, _, _) => new(true, 0x6000, replacement));
        Assert.Equal(HybridCpuManagedExceptionStatusV1.Handled, replaced.Status);
        Assert.Equal(replacement, replaced.ExceptionTypeId);
        Assert.Contains("throw-in-finally", replaced.Trace);
    }

    [Fact]
    public void RecursiveUnwindKeepsExceptionGcRootAndUnhandledPolicyIsDeterministic()
    {
        HybridCpuManagedTypeSystemV1 types = Types();
        ulong exception = Type(types, "System.Exception").TypeId;
        HybridCpuManagedExceptionRuntimeV1 runtime = new HybridCpuManagedExceptionRuntimeV1(types,
            [Registration("recursive", 1000, 1000, [])]);
        HybridCpuManagedEhFrameSnapshotV1[] frames = Enumerable.Range(0, 32)
            .Select(_ => Frame("recursive", 1010, returnPc: 1010)).ToArray();
        int gcCalls = 0;
        HybridCpuManagedExceptionDispatchResultV1 first = runtime.Dispatch(0x7000, exception, frames,
            gcSafepoint: (root, snapshot) => { gcCalls++; return root == 0x7000 && snapshot.Count == 32; });
        HybridCpuManagedExceptionDispatchResultV1 second = runtime.Dispatch(0x7000, exception, frames,
            gcSafepoint: static (_, _) => true);
        Assert.Equal(HybridCpuManagedExceptionStatusV1.UnhandledTermination, first.Status);
        Assert.Equal(first.ResultDigest, second.ResultDigest);
        Assert.Equal(32, first.UnwoundFrames);
        Assert.Equal(1, gcCalls);
    }

    [Fact]
    public void CorruptEhAndUnwindMetadataFailClosedBeforeDispatch()
    {
        HybridCpuManagedTypeSystemV1 types = Types();
        HybridCpuManagedEhMethodRegistrationV1 good = Registration("method", 1000, 1000, []);
        byte[] badEh = (byte[])good.EhInfo.Clone(); badEh[0] ^= 0xff;
        Assert.Throws<ArgumentException>(() => new HybridCpuManagedExceptionRuntimeV1(types,
            [good with { EhInfo = badEh }]));
        byte[] badUnwind = (byte[])good.UnwindInfo.Clone(); badUnwind[24] ^= 1;
        Assert.Throws<ArgumentException>(() => new HybridCpuManagedExceptionRuntimeV1(types,
            [good with { UnwindInfo = badUnwind }]));
        HybridCpuManagedEhMethodRegistrationV1 unknownCatch = Registration("unknown", 2000, 1000,
            [new(HybridCpuManagedEhClauseKindV1.Catch, 0, 100, 100, 100, ulong.MaxValue, 0)]);
        Assert.Throws<ArgumentException>(() => new HybridCpuManagedExceptionRuntimeV1(types, [unknownCatch]));
        Assert.Throws<ArgumentException>(() => new HybridCpuManagedExceptionRuntimeV1(types,
            [good, Registration("overlap", 1500, 1000, [])]));
        Assert.Throws<ArgumentException>(() => new HybridCpuManagedExceptionRuntimeV1(types,
            [good with { CodeStartOffsetBytes = int.MaxValue - 10, CodeSizeBytes = 100 }]));
        ulong exception = Type(types, "System.Exception").TypeId;
        HybridCpuManagedExceptionRuntimeV1 chainRuntime = new HybridCpuManagedExceptionRuntimeV1(types,
            [good, Registration("caller", 3000, 1000, [])]);
        HybridCpuManagedExceptionDispatchResultV1 badChain = chainRuntime.Dispatch(1, exception,
            [Frame("method", 1010, returnPc: 3999), Frame("caller", 3010)]);
        Assert.Equal(HybridCpuManagedExceptionStatusV1.InvalidMetadata, badChain.Status);
        Assert.Contains("invalid-unwind-chain:0", badChain.Trace);
    }

    [Fact]
    public void GcAcceptsOnlyDigestBoundFinalManagedUnwindDuringEhSafepoint()
    {
        ManagedEhMethodPlanV1 plan = Plan();
        IrRegisterAllocationResultV1 allocation = Allocation(plan.InstructionIdentityPrefix);
        ManagedEhFinalizationArtifactV1 finalized = new ManagedEhMetadataFinalizerV1().Finalize(plan, allocation);
        Assert.Equal(HybridCpuManagedMetadataStatusV1.Finalized, finalized.Status);
        HybridCpuGcInfoEncodingResultV1 gcInfo = HybridCpuManagedAbiEncodingV1.EncodeGcInfo(
            [new(0, HybridCpuSafepointCategoryV1.CallSite, [])]);
        Assert.Equal(HybridCpuPlatformFactStatus.Supported, gcInfo.Status);
        string unwindDigest = Convert.ToHexString(SHA256.HashData(finalized.UnwindInfo)).ToLowerInvariant();
        byte[] codeManager = HybridCpuManagedAbiEncodingV1.EncodeCodeManagerMetadata(
            [new(plan.MethodIdentity, 0, allocation.FinalBundles!.BlockResults.Sum(static row => row.Bundles.Count) *
                HybridCpuBundleSerializer.BundleSizeBytes, gcInfo.Digest, unwindDigest)]);
        HybridCpuManagedAbiFamilyV1 abi = HybridCpuManagedAbiFamilyV1.Default;
        HybridCpuManagedStackMapRegistrationV1 registration = new HybridCpuManagedStackMapRegistrationV1(plan.MethodIdentity, gcInfo.Bytes, codeManager,
            abi.ContractDigest, abi.TargetContractDigest, abi.NativeAbiDigest,
            HybridCpuManagedAbiFamilyV1.RuntimePackRevision, finalized.UnwindInfo);
        HybridCpuManagedTypeSystemV1 types = Types();
        HybridCpuManagedHeapAllocatorV1 heap = Heap(types);
        HybridCpuManagedNonMovingGcV1 gc = new HybridCpuManagedNonMovingGcV1(types, heap, abi.ContractDigest, abi.TargetContractDigest,
            abi.NativeAbiDigest, HybridCpuManagedAbiFamilyV1.RuntimePackRevision);
        HybridCpuManagedNonMovingGcResultV1 accepted = gc.Collect(new([registration], [], []),
            HybridCpuManagedNonMovingGcOptionsV1.Qualification);
        Assert.True(accepted.IsSuccess, accepted.Reason);

        byte[] corrupt = finalized.UnwindInfo.ToArray();
        corrupt[^1] ^= 1;
        HybridCpuManagedNonMovingGcResultV1 rejected = gc.Collect(new(
            [registration with { UnwindInfo = corrupt }], [], []), HybridCpuManagedNonMovingGcOptionsV1.Qualification);
        Assert.Equal(HybridCpuManagedNonMovingGcStatusV1.InvalidMetadata, rejected.Status);

        byte[] malformedRows = HybridCpuManagedUnwindCodecV2.Encode(new(HybridCpuManagedFrameKindV1.Managed,
            HybridCpuManagedCfaBaseV1.StackPointer, 0, HybridCpuNativeAbiContractV2.ReturnAddressRegister, null,
            [new(8, -8)]));
        BinaryPrimitives.WriteInt32LittleEndian(malformedRows.AsSpan(28), 0);
        string malformedDigest = Convert.ToHexString(SHA256.HashData(malformedRows)).ToLowerInvariant();
        byte[] malformedCodeManager = HybridCpuManagedAbiEncodingV1.EncodeCodeManagerMetadata(
            [new(plan.MethodIdentity, 0, allocation.FinalBundles.BlockResults.Sum(static row => row.Bundles.Count) *
                HybridCpuBundleSerializer.BundleSizeBytes, gcInfo.Digest, malformedDigest)]);
        HybridCpuManagedNonMovingGcResultV1 malformed = gc.Collect(new(
            [registration with { CodeManagerMetadata = malformedCodeManager, UnwindInfo = malformedRows }], [], []),
            HybridCpuManagedNonMovingGcOptionsV1.Qualification);
        Assert.Equal(HybridCpuManagedNonMovingGcStatusV1.InvalidMetadata, malformed.Status);
    }

    [Fact]
    public void FinalEhSectionsSurviveLinkLoaderBootstrapAndOrdinaryIseExecution()
    {
        ManagedEhMethodPlanV1 plan = Plan();
        IrRegisterAllocationResultV1 allocation = Allocation(plan.InstructionIdentityPrefix);
        ManagedEhFinalizationArtifactV1 metadata = new ManagedEhMetadataFinalizerV1().Finalize(plan, allocation);
        Assert.Equal(HybridCpuManagedMetadataStatusV1.Finalized, metadata.Status);
        byte[] code = ExecutableCode(allocation);
        HybridCpuObjectArtifactV1 obj = Object("managed_eh_entry", code, metadata.ObjectSections);
        HybridCpuStaticLinkArtifactV1 link = new HybridCpuStaticLinkerV1().Link([new("phase09", obj.Bytes)]);
        Assert.Equal(HybridCpuLinkStatusV1.Success, link.Status);
        HybridCpuLinkedSymbolV1 symbol = Assert.Single(link.Symbols, static row => row.Name == "managed_eh_entry");
        HybridCpuGcInfoEncodingResultV1 gc = HybridCpuManagedAbiEncodingV1.EncodeGcInfo([]);
        string unwindDigest = Convert.ToHexString(SHA256.HashData(metadata.UnwindInfo)).ToLowerInvariant();
        HybridCpuCodeManagerRegistrationV1 codeManager = new HybridCpuCodeManagerRegistrationV1("managed_eh_entry",
            checked((int)(symbol.Address - link.ImageBase)), checked((int)symbol.Size), gc.Digest, unwindDigest);
        HybridCpuImageRuntimeBootstrapDescriptorV1 descriptor = HybridCpuImageRuntimeBootstrapContractV1.Create(
            HybridCpuManagedAbiFamilyV1.Default.ContractDigest, "managed_eh_entry", "managed_eh_entry", [], [codeManager]);
        HybridCpuRestrictedImageV1 image = new HybridCpuRestrictedImageBuilderV1().Build(
            new(link, "managed_eh_entry", RuntimeBootstrap: descriptor));
        Assert.Equal(HybridCpuStartupStatusV1.Success, image.Status);
        HybridCpuRestrictedImageV1 loaded = new HybridCpuRestrictedImageBuilderV1().Inspect(image.PackageBytes);
        Assert.Equal(HybridCpuStartupStatusV1.Success, loaded.Status);
        Assert.NotNull(loaded.RuntimeBootstrap);

        DeterministicRuntimeKernelV1 kernel = new DeterministicRuntimeKernelV1();
        Assert.True(kernel.Boot(new(HybridCpuPlatformContractV1.ContractDigest, new string('a', 64),
            0x100000, 4096, 0x100000, 0x200000, 4096, 0)).IsSuccess);
        HybridCpuManagedBootstrapResultV1 boot = new HybridCpuManagedBootstrapRuntimeV1(
            HybridCpuManagedAbiFamilyV1.Default.ContractDigest).Bootstrap(loaded.RuntimeBootstrap!, kernel,
            new Dictionary<string, HybridCpuRuntimeHelperEntryV1>());
        Assert.True(boot.IsSuccess, boot.Reason);
        _ = Execute(loaded);
        Assert.Contains(link.Sections, static row => row.Kind == HybridCpuObjectSectionKind.ExceptionHandling);
        Assert.Contains(link.Sections, static row => row.Kind == HybridCpuObjectSectionKind.Unwind);
    }

    private static ManagedEhMethodPlanV1 Plan(ulong catchTypeId = 1)
    {
        const string prefix = "cil:phase09:test:il_";
        ManagedEhClausePlanV1[] clauses =
        [
            new(HybridCpuManagedEhClauseKindV1.Finally, 0, 2, 2, 1, null, 0, 0),
            new(HybridCpuManagedEhClauseKindV1.Catch, 0, 3, 3, 1, "System.Exception", catchTypeId, 1)
        ];
        ManagedEhOperationPlanV1[] operations =
        [
            new(ManagedEhOperationKindV1.Throw, 1, -1, "__hybridcpu_managed_throw"),
            new(ManagedEhOperationKindV1.EndFinally, 2, -1, "__hybridcpu_managed_endfinally"),
            new(ManagedEhOperationKindV1.Leave, 3, 4, string.Empty)
        ];
        ManagedEhMethodPlanV1 draft = new ManagedEhMethodPlanV1("Phase09.Test", prefix, 5, clauses, operations, string.Empty);
        return draft with { ContractDigest = ManagedEhPlanContractV1.ComputeDigest(draft.MethodIdentity,
            draft.InstructionIdentityPrefix, draft.MethodBodySize, draft.Clauses, draft.Operations) };
    }

    private static IrRegisterAllocationResultV1 Allocation(string prefix)
    {
        const int count = 6;
        HybridCpuInstructionWord[] words = new HybridCpuInstructionWord[count];
        for (int index = 0; index < count - 1; index++)
        {
            words[index] = new()
            {
                OpCode = (uint)HybridCpuOpcode.ADDI,
                DataTypeValue = HybridCpuDataType.INT64,
                PredicateMask = byte.MaxValue,
                Immediate = 1,
                Word1 = HybridCpuInstructionWord.PackArchRegs(HybridCpuInstructionWord.NoArchReg,
                    HybridCpuInstructionWord.NoArchReg, HybridCpuInstructionWord.NoArchReg)
            };
        }
        words[^1] = new()
        {
            OpCode = (uint)HybridCpuOpcode.JALR,
            DataTypeValue = HybridCpuDataType.INT64,
            PredicateMask = byte.MaxValue,
            VirtualThreadId = 0,
            Word1 = HybridCpuInstructionWord.PackArchRegs(0,
                (byte)HybridCpuNativeAbiContractV2.ReturnAddressRegister, HybridCpuInstructionWord.NoArchReg)
        };
        IrProgram raw = new HybridCpuIrBuilder().BuildProgram(0, words);
        List<IrValueAccessV1> accesses = new List<IrValueAccessV1>();
        IrInstruction[] instructions = raw.Instructions.Select((row, index) =>
        {
            if (index == count - 1)
                return row with { StableIdentity = "phase09:return" };
            string definition = $"phase09:step:{index}";
            string? use = index == 0 ? null : $"phase09:step:{index - 1}";
            IrOperand def = new IrOperand(IrOperandKind.VirtualValue, 0, definition);
            IrOperand[] uses = use is null ? [] : [new(IrOperandKind.VirtualValue, 0, use)];
            accesses.Add(new(definition, index, IrValueAccessKind.Def));
            if (use is not null) accesses.Add(new(use, index, IrValueAccessKind.Use));
            return row with
            {
                StableIdentity = $"{prefix}{index:x4}",
                Operands = row.Operands.Concat([def]).Concat(uses).ToArray(),
                Annotation = row.Annotation with
                {
                    Defs = row.Annotation.Defs.Concat([def]).ToArray(),
                    Uses = row.Annotation.Uses.Concat(uses).ToArray()
                }
            };
        }).ToArray();
        IrBasicBlock[] blocks = raw.BasicBlocks.Select(block => block with
            { Instructions = block.Instructions.Select(row => instructions[row.Index]).ToArray() }).ToArray();
        IrProgram program = raw with
        {
            Instructions = instructions,
            ControlFlowGraph = raw.ControlFlowGraph with { Blocks = blocks },
            ValueFlow = new("hybridcpu.value-flow/v1", 1,
                Enumerable.Range(0, count - 1).Select(index => IntegerValue($"phase09:step:{index}")).ToArray(),
                accesses)
        };
        IrProgramSchedule schedule = new HybridCpuLocalListScheduler().ScheduleProgram(program);
        IrProgramBundlingResult bundles = new HybridCpuBundleFormer().BundleProgram(schedule);
        HybridCpuMiiResourceModelV1 resources = HybridCpuMiiResourceModelV1.Create(
            new HybridCpuMachineTopologyV1(64, 64, 4, 8, 2, 16), 8);
        IrRegisterAllocationResultV1 allocation = new HybridCpuScheduleAwareRegisterAllocatorV1().Allocate(
            schedule, bundles, resourceModel: resources, options: HybridCpuRegisterAllocationOptionsV1.Qualification,
            fixedFrameSlots:
            [
                new(ManagedEhFrameHomesV1.FinallyContinuationTokenSlot, 8, 8)
            ]);
        Assert.True(allocation.Status == IrRegisterAllocationStatusV1.Allocated,
            $"{allocation.Status}: {allocation.Reason}");
        return allocation;

        static IrVirtualValueV1 IntegerValue(string identity)
        {
            HybridCpuTargetMachineContractV1 target = HybridCpuTargetMachineContractV1.Default;
            int[] registers = HybridCpuNativeAbiContractV2.Default.AllocatableRegisters.ToArray();
            return new(identity, new(IrCanonicalValueKind.Integer, 64, IsSigned: true),
                IrVirtualValueClass.ScalarInteger, new(64, 1, false, true,
                    HybridCpuArchitecturalRegisterClass.ScalarInteger64, null, null, 0, ["native-scalar"], null,
                    registers, registers.Select(register => target.ArchitecturalRegisters[register].RegisterGroup)
                        .Distinct().Order().ToArray(), target.ContractDigest, null));
        }
    }

    private static byte[] ExecutableCode(IrRegisterAllocationResultV1 allocation)
    {
        IReadOnlyList<HybridCpuInstructionBundle> bundles = new HybridCpuBundleLowerer().LowerProgram(allocation.FinalBundles!);
        return new HybridCpuBundleSerializer().SerializeProgram(bundles);
    }

    private static HybridCpuObjectArtifactV1 Object(string symbol, byte[] code,
        IReadOnlyList<HybridCpuObjectSectionV1> metadata)
    {
        HybridCpuObjectSectionV1[] sections =
        [new(".text", HybridCpuObjectSectionKind.Code, HybridCpuBundleSerializer.BundleSizeBytes, code, (ulong)code.Length), .. metadata];
        return new HybridCpuObjectWriterV1().Write(new(sections,
            [new(symbol, HybridCpuSymbolBinding.Global, HybridCpuSymbolVisibility.Default, ".text", 0, (ulong)code.Length, true)],
            [], HybridCpuTargetPlatformContractV1.Default.ContractDigest, HybridCpuManagedAbiFamilyV1.Default.ContractDigest));
    }

    private static HybridCpuManagedTypeSystemV1 Types()
    {
        HybridCpuManagedTypeSystemBuildV1 build = new HybridCpuManagedTypeSystemBuilderV1().Build([
            new("System.Exception", HybridCpuManagedTypeKindV1.Class, null, [], []),
            new("System.ArgumentException", HybridCpuManagedTypeKindV1.Class, "System.Exception", [], []),
            new("Phase09.ReplacementException", HybridCpuManagedTypeKindV1.Class, "System.Exception", [], [])
        ]);
        Assert.True(build.IsSuccess, build.Reason);
        return build.TypeSystem!;
    }

    private static HybridCpuManagedTypeDescriptorV1 Type(HybridCpuManagedTypeSystemV1 types, string identity) =>
        Assert.Single(types.Descriptors, row => row.StableIdentity == identity);

    private static HybridCpuManagedHeapAllocatorV1 Heap(HybridCpuManagedTypeSystemV1 types)
    {
        DeterministicRuntimeKernelV1 kernel = new DeterministicRuntimeKernelV1();
        Assert.True(kernel.Boot(new(HybridCpuPlatformContractV1.ContractDigest, new string('a', 64),
            0x0010_0000, 4096, 0x0010_0000, 0x0020_0000, 4096, 0)).IsSuccess);
        HybridCpuManagedHeapAllocatorV1 heap = new HybridCpuManagedHeapAllocatorV1(kernel, types,
            HybridCpuManagedHeapOptionsV1.Create(0x4000_0000, 4096, 4096, -9));
        Assert.True(heap.Initialize().IsSuccess);
        return heap;
    }

    private static HybridCpuManagedEhMethodRegistrationV1 Registration(string method, int start, int size,
        IReadOnlyList<HybridCpuManagedEhClauseRegistrationV1> clauses) =>
        new(method, start, size, EncodeEh(clauses), HybridCpuManagedUnwindCodecV2.Encode(new(
            HybridCpuManagedFrameKindV1.Managed, HybridCpuManagedCfaBaseV1.StackPointer, 0,
            HybridCpuNativeAbiContractV2.ReturnAddressRegister, null, [])));

    private static HybridCpuManagedEhFrameSnapshotV1 Frame(string method, int pc, ulong stackPointer = 0x8000,
        ulong returnPc = 0) => new(method, pc, stackPointer, returnPc);

    private static byte[] EncodeEh(IReadOnlyList<HybridCpuManagedEhClauseRegistrationV1> clauses)
    {
        byte[] bytes = new byte[12 + clauses.Count * 40];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, HybridCpuManagedEhSchemaV1.EhMagic);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), HybridCpuManagedEhSchemaV1.SchemaVersion);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), clauses.Count);
        for (int index = 0, offset = 12; index < clauses.Count; index++, offset += 40)
        {
            HybridCpuManagedEhClauseRegistrationV1 row = clauses[index];
            bytes[offset] = (byte)row.Kind;
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset + 4), row.TryStartOffsetBytes);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset + 8), row.TrySizeBytes);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset + 12), row.HandlerStartOffsetBytes);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset + 16), row.HandlerSizeBytes);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(offset + 20), row.CatchTypeId);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset + 28), row.Ordinal);
        }
        return bytes;
    }

    private static ulong Execute(HybridCpuRestrictedImageV1 image)
    {
        HybridCpuStartupRegisterStateV1 registers = image.InitialRegisters!;
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
            core.WriteCommittedArch(0, registers.StackPointerRegister, registers.StackPointer);
            core.WriteCommittedArch(0, registers.ReturnAddressRegister, registers.ReturnAddress);
            int retired = 0;
            while (core.ReadCommittedPc(0) != HybridCpuRestrictedStartupOptionsV1.Production.ReturnSentinel && retired++ < 256)
            {
                ulong pc = core.ReadCommittedPc(0);
                VLIW_Instruction[] bundle = CompilerRefPlan7Phase03ManagedHeapAllocatorTests.ReadBundle(image, pc);
                bool control = bundle.Any(static instruction => instruction.OpCode is
                    >= (uint)Processor.CPU_Core.InstructionsEnum.JAL and <= (uint)Processor.CPU_Core.InstructionsEnum.BGEU);
                core.TestRunDecodeStageWithFetchedBundle(bundle, pc);
                core.TestRunExecuteStageFromCurrentDecodeState();
                core.TestRunMemoryAndWriteBackStagesFromCurrentExecuteState();
                if (!control || core.ReadCommittedPc(0) == pc)
                    core.WriteCommittedPc(0, checked(pc + (ulong)HybridCpuBundleSerializer.BundleSizeBytes));
            }
            Assert.Equal(HybridCpuRestrictedStartupOptionsV1.Production.ReturnSentinel, core.ReadCommittedPc(0));
            return core.ReadArch(0, registers.ReturnValueRegister);
        }
        finally
        {
            Processor.MainMemory = originalMemory;
            Processor.CurrentProcessorMode = originalMode;
            Processor.Memory = originalSubsystem;
        }
    }

}
