using HybridCPU.Compiler.Core.Target.Managed;
using HybridCPU.Compiler.Core.Target;
using HybridCPU.ManagedRuntime;
using System.Security.Cryptography;
using System.Text;

namespace HybridCPU_ISE.Tests.CompilerTests;

public sealed class CompilerRefPlan5Phase25BManagedGcRuntimeTests
{
    [Fact]
    public void RuntimeContractIsIndependentPinnedAndProductionDisabled()
    {
        HybridCpuManagedGcRuntimeContractV1 contract = HybridCpuManagedGcRuntimeContractV1.Default;

        Assert.Equal("hybridcpu.managed-gc-runtime/v1", HybridCpuManagedGcRuntimeContractV1.SchemaId);
        Assert.Equal("2ef28903d8b273f848283c0940899e0390183d8736980ad0c251b27e121704d9",
            HybridCpuManagedGcRuntimeContractV1.ManagedAbiDigest);
        Assert.NotEqual(HybridCpuManagedGcRuntimeContractV1.ManagedAbiDigest,
            HybridCpuManagedAbiFamilyV1.Default.ContractDigest);
        Assert.Equal("3b3714582295e0a0daa111841b2fe96aae610788ec7e27b222b7552dd5879431",
            HybridCpuManagedGcRuntimeContractV1.TargetPlatformDigest);
        Assert.NotEqual(HybridCpuManagedGcRuntimeContractV1.TargetPlatformDigest,
            HybridCpuTargetPlatformContractV1.Default.ContractDigest);
        Assert.Equal("3a4d4a4aebed5e5df40a3615b4e40c301181cb71bf281d958470fbee308317a4",
            HybridCpuManagedGcRuntimeContractV1.NativeAbiDigest);
        Assert.NotEqual(HybridCpuManagedGcRuntimeContractV1.NativeAbiDigest,
            HybridCpuManagedAbiFamilyV1.Default.NativeAbiDigest);
        Assert.False(HybridCpuManagedGcRuntimeOptionsV1.Production.EnableMovingCollection);
        Assert.True(HybridCpuManagedGcRuntimeOptionsV1.Qualification.EnableMovingCollection);
        Assert.Equal("8a138697dc064cfa45910a42c9552f66c12ca7233490b40ba23571b807c30617",
            contract.ContractDigest);
    }

    [Fact]
    public void CompilerMapDrivesRealRegisterRootWalkAndMovingCollection()
    {
        RuntimeSubject subject = Subject();
        ulong root = 0x2000;
        ulong child = 0x3000;
        ulong dead = 0x4000;
        HybridCpuManagedRuntimeFrameV1 frame = Frame(subject, root);
        HybridCpuManagedHeapV1 heap = new HybridCpuManagedHeapV1(
        [
            new(root, [1], [child]),
            new(child, [2], []),
            new(dead, [3], [])
        ]);

        HybridCpuManagedGcCollectionResultV1 result = Collect(subject, [frame], heap);

        Assert.Equal(HybridCpuManagedGcRuntimeStatusV1.Collected, result.Status);
        Assert.Equal(1, result.RootCount);
        Assert.Equal(1, result.ReclaimedObjectCount);
        Assert.Equal(2, result.Heap.Objects.Count);
        Assert.NotEqual(root, result.ForwardingAddresses[root]);
        Assert.Equal(result.ForwardingAddresses[root],
            result.Thread.Frames[0].Registers[subject.RegisterId]);
        HybridCpuManagedHeapObjectV1 movedRoot = result.Heap.Objects.Single(item =>
            item.Address == result.ForwardingAddresses[root]);
        Assert.Equal(result.ForwardingAddresses[child], Assert.Single(movedRoot.References));
        Assert.DoesNotContain(dead, result.ForwardingAddresses.Keys);
    }

    [Fact]
    public void ProductionDefaultDoesNotMoveOrReclaim()
    {
        RuntimeSubject subject = Subject();
        HybridCpuManagedRuntimeFrameV1 frame = Frame(subject, 0x2000);
        HybridCpuManagedHeapV1 heap = new HybridCpuManagedHeapV1([new(0x2000, [1], [])]);
        HybridCpuManagedGcCollectionRequestV1 request = new HybridCpuManagedGcCollectionRequestV1([subject.Registration], new([frame]), heap);

        HybridCpuManagedGcCollectionResultV1 result = new HybridCpuManagedGcRuntimeV1().Collect(request);

        Assert.Equal(HybridCpuManagedGcRuntimeStatusV1.Disabled, result.Status);
        Assert.Same(heap, result.Heap);
        Assert.Empty(result.ForwardingAddresses);
    }

    [Fact]
    public void HeapAndFrameInputOrderDoNotChangeMovingResult()
    {
        RuntimeSubject subject = Subject();
        HybridCpuManagedRuntimeFrameV1 firstFrame = Frame(subject, 0x3000);
        HybridCpuManagedRuntimeFrameV1 secondFrame = Frame(subject, 0x2000);
        HybridCpuManagedHeapObjectV1[] objects =
        [
            new(0x4000, [4], []),
            new(0x3000, [3], [0x2000]),
            new(0x2000, [2], [])
        ];

        HybridCpuManagedGcCollectionResultV1 first = Collect(subject,
            [firstFrame, secondFrame], new(objects));
        HybridCpuManagedGcCollectionResultV1 second = Collect(subject,
            [firstFrame, secondFrame], new(objects.Reverse().ToArray()));

        Assert.Equal(HybridCpuManagedGcRuntimeStatusV1.Collected, first.Status);
        Assert.Equal(first.ResultDigest, second.ResultDigest);
        Assert.Equal(first.ForwardingAddresses.OrderBy(static pair => pair.Key),
            second.ForwardingAddresses.OrderBy(static pair => pair.Key));
    }

    [Fact]
    public void StackRootLocationIsWalkedAndUpdated()
    {
        HybridCpuGcInfoEncodingResultV1 gc = HybridCpuManagedAbiEncodingV1.EncodeGcInfo(
        [
            new(0, HybridCpuSafepointCategoryV1.CallSite,
                [new("stack-root", HybridCpuGcReferenceKindV1.ObjectReference,
                    HybridCpuGcLocationKindV1.Stack, null, 16)])
        ]);
        byte[] codeManager = HybridCpuManagedAbiEncodingV1.EncodeCodeManagerMetadata(
            [new("StackMethod", 0x1000, 32, gc.Digest, null)]);
        HybridCpuManagedMethodRegistrationV1 registration = Registration("StackMethod", gc.Bytes, codeManager);
        ulong[] registers = new ulong[64];
        HybridCpuManagedRuntimeFrameV1 frame = new HybridCpuManagedRuntimeFrameV1("StackMethod", 0x1000, registers,
            new Dictionary<int, ulong> { [16] = 0x2000 });
        HybridCpuManagedGcCollectionRequestV1 request = new HybridCpuManagedGcCollectionRequestV1([registration], new([frame]),
            new([new(0x2000, [1], [])]));

        HybridCpuManagedGcCollectionResultV1 result = new HybridCpuManagedGcRuntimeV1().Collect(
            request, HybridCpuManagedGcRuntimeOptionsV1.Qualification);

        Assert.Equal(HybridCpuManagedGcRuntimeStatusV1.Collected, result.Status);
        Assert.Equal(result.ForwardingAddresses[0x2000], result.Thread.Frames[0].StackSlots[16]);
    }

    [Fact]
    public void AbiSkewAndCorruptedMetadataAreRejectedBeforeCollection()
    {
        RuntimeSubject subject = Subject();
        HybridCpuManagedRuntimeFrameV1 frame = Frame(subject, 0x2000);
        HybridCpuManagedHeapV1 heap = new([new(0x2000, [1], [])]);
        HybridCpuManagedMethodRegistrationV1 skew = subject.Registration with { RuntimePackRevision = "wrong" };
        byte[] corruptedBytes = subject.Registration.GcInfo.ToArray();
        corruptedBytes[^1] ^= 1;
        HybridCpuManagedMethodRegistrationV1 corrupted = subject.Registration with { GcInfo = corruptedBytes };

        HybridCpuManagedGcCollectionResultV1 skewResult = Collect(skew, [frame], heap);
        HybridCpuManagedGcCollectionResultV1 corruptedResult = Collect(corrupted, [frame], heap);

        Assert.Equal(HybridCpuManagedGcRuntimeStatusV1.Unsupported, skewResult.Status);
        Assert.Equal(HybridCpuManagedGcRuntimeStatusV1.InvalidMetadata, corruptedResult.Status);
        Assert.Same(heap, skewResult.Heap);
        Assert.Same(heap, corruptedResult.Heap);
    }

    [Fact]
    public void InvalidRootOrObjectEdgeFailsTransactionally()
    {
        RuntimeSubject subject = Subject();
        HybridCpuManagedRuntimeFrameV1 missingRoot = Frame(subject, 0x9998);
        HybridCpuManagedRuntimeFrameV1 validRoot = Frame(subject, 0x2000);
        HybridCpuManagedHeapV1 empty = new([]);
        HybridCpuManagedHeapV1 brokenEdge = new([new(0x2000, [1], [0x9998])]);

        HybridCpuManagedGcCollectionResultV1 rootResult = Collect(subject, [missingRoot], empty);
        HybridCpuManagedGcCollectionResultV1 edgeResult = Collect(subject, [validRoot], brokenEdge);

        Assert.Equal(HybridCpuManagedGcRuntimeStatusV1.InvalidState, rootResult.Status);
        Assert.Equal(HybridCpuManagedGcRuntimeStatusV1.InvalidState, edgeResult.Status);
        Assert.Empty(rootResult.ForwardingAddresses);
        Assert.Empty(edgeResult.ForwardingAddresses);
        Assert.Same(empty, rootResult.Heap);
        Assert.Same(brokenEdge, edgeResult.Heap);
    }

    [Fact]
    public void NullRootAllowsUnreachableHeapToBeReclaimed()
    {
        RuntimeSubject subject = Subject();

        HybridCpuManagedGcCollectionResultV1 result = Collect(subject, [Frame(subject, 0)],
            new([new(0x2000, [1], [])]));

        Assert.Equal(HybridCpuManagedGcRuntimeStatusV1.Collected, result.Status);
        Assert.Empty(result.Heap.Objects);
        Assert.Equal(1, result.ReclaimedObjectCount);
    }

    [Fact]
    public void ForgedOptionsAndObjectBudgetFailClosed()
    {
        RuntimeSubject subject = Subject();
        HybridCpuManagedGcCollectionRequestV1 request = new([subject.Registration],
            new([Frame(subject, 0)]), new([new(0x2000, [1], [])]));
        HybridCpuManagedGcRuntimeOptionsV1 forged = HybridCpuManagedGcRuntimeOptionsV1.Qualification with
        {
            OptionsDigest = new('0', 64)
        };
        HybridCpuManagedGcRuntimeOptionsV1 bounded = HybridCpuManagedGcRuntimeOptionsV1.Create(
            true, new(1, 1, 1, 1, 1));
        HybridCpuManagedGcCollectionRequestV1 tooMany = request with
        {
            Heap = new([new(0x2000, [1], []), new(0x3000, [2], [])])
        };

        Assert.Equal(HybridCpuManagedGcRuntimeStatusV1.InvalidState,
            new HybridCpuManagedGcRuntimeV1().Collect(request, forged).Status);
        Assert.Equal(HybridCpuManagedGcRuntimeStatusV1.BudgetExhausted,
            new HybridCpuManagedGcRuntimeV1().Collect(tooMany, bounded).Status);
    }

    private static RuntimeSubject Subject()
    {
        CompilerRefPlan5Phase25AManagedMetadataFinalizationTests.AllocatedSubject allocated =
            CompilerRefPlan5Phase25AManagedMetadataFinalizationTests.AllocateSubject("root");
        HybridCpuManagedMetadataArtifactV1 artifact =
            CompilerRefPlan5Phase25AManagedMetadataFinalizationTests.Finalize(
                CompilerRefPlan5Phase25AManagedMetadataFinalizationTests.Request(allocated, "root"));
        Assert.Equal(HybridCpuManagedMetadataStatusV1.Finalized, artifact.Status);
        HybridCpuGcReferenceLocationV1 root = Assert.Single(artifact.Safepoints.Single().LiveReferences);
        return new(Registration("Phase25A.ManagedMethod", artifact.GcInfo, artifact.CodeManagerMetadata),
            artifact.Safepoints.Single().CodeOffsetBytes, root.RegisterId!.Value);
    }

    private static HybridCpuManagedMethodRegistrationV1 Registration(
        string method,
        byte[] gcInfo,
        byte[] codeManager)
    {
        byte[] historicalCodeManager = codeManager.ToArray();
        string historicalGcDigest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"gc-info|{HybridCpuManagedGcRuntimeContractV1.ManagedAbiDigest}|{Convert.ToHexString(gcInfo)}")))
            .ToLowerInvariant();
        Convert.FromHexString(historicalGcDigest).CopyTo(historicalCodeManager, 28);
        return new(method, gcInfo, historicalCodeManager,
            HybridCpuManagedGcRuntimeContractV1.ManagedAbiDigest,
            HybridCpuManagedGcRuntimeContractV1.TargetPlatformDigest,
            HybridCpuManagedGcRuntimeContractV1.NativeAbiDigest,
            HybridCpuManagedGcRuntimeContractV1.RuntimePackRevision);
    }

    private static HybridCpuManagedRuntimeFrameV1 Frame(RuntimeSubject subject, ulong root)
    {
        ulong[] registers = new ulong[64];
        registers[subject.RegisterId] = root;
        return new("Phase25A.ManagedMethod", subject.CodeOffset, registers,
            new Dictionary<int, ulong>());
    }

    private static HybridCpuManagedGcCollectionResultV1 Collect(
        RuntimeSubject subject,
        IReadOnlyList<HybridCpuManagedRuntimeFrameV1> frames,
        HybridCpuManagedHeapV1 heap) => Collect(subject.Registration, frames, heap);

    private static HybridCpuManagedGcCollectionResultV1 Collect(
        HybridCpuManagedMethodRegistrationV1 registration,
        IReadOnlyList<HybridCpuManagedRuntimeFrameV1> frames,
        HybridCpuManagedHeapV1 heap) => new HybridCpuManagedGcRuntimeV1().Collect(
            new([registration], new(frames), heap), HybridCpuManagedGcRuntimeOptionsV1.Qualification);

    private sealed record RuntimeSubject(
        HybridCpuManagedMethodRegistrationV1 Registration,
        int CodeOffset,
        int RegisterId);
}
