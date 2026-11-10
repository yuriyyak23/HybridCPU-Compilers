using HybridCPU.Compiler.Core.Target;
using Xunit;

namespace HybridCPU_ISE.Tests.CompilerTests;

public sealed class CompilerRefPlan5Phase08B2NativeAbiCompletionTests
{
    private static HybridCpuNativeAbiContractV2 Contract => HybridCpuNativeAbiContractV2.Default;

    [Fact]
    public void Contract_IsVersionedBoundAndDeterministic()
    {
        Assert.Equal("hybridcpu.native-abi", HybridCpuNativeAbiContractV2.SchemaId);
        Assert.Equal(2, HybridCpuNativeAbiContractV2.SchemaMajor);
        Assert.Equal(HybridCpuTargetMachineContractV1.Default.ContractDigest, Contract.TargetContractDigest);
        Assert.Equal(HybridCpuTargetPlatformContractV1.Default.ContractDigest, Contract.PredecessorPlatformContractDigest);
        Assert.Equal("80f94f7a7a87722d4dc0d62eb505e51302f9bab9cd340d35f84514d87ee56944", Contract.ContractDigest);
        Assert.Equal(Contract.ContractDigest, HybridCpuNativeAbiContractV2.Default.ContractDigest);
    }

    [Fact]
    public void RegisterRoles_AreCompleteDisjointAndTraceableToArchitecturalNamespace()
    {
        Assert.Equal([10, 11, 12, 13, 14, 15, 16, 17], Contract.ArgumentRegisters);
        Assert.Equal([10, 11], Contract.ReturnRegisters);
        Assert.Equal([0, 1, 2, 3, 4, 8], Contract.ReservedRegisters);
        Assert.Equal(2, HybridCpuNativeAbiContractV2.StackPointerRegister);
        Assert.Equal(8, HybridCpuNativeAbiContractV2.FramePointerRegister);
        Assert.Empty(Contract.ReservedRegisters.Intersect(Contract.AllocatableRegisters));
        Assert.Equal(Enumerable.Range(0, 32),
            Contract.ReservedRegisters.Concat(Contract.AllocatableRegisters).Order());
        Assert.All(Contract.CallerSavedRegisters.Concat(Contract.CalleeSavedRegisters), register =>
            Assert.InRange(register, 0, HybridCpuTargetMachineContractV1.ArchitecturalRegisterCount - 1));
        Assert.Empty(Contract.CallerSavedRegisters.Intersect(Contract.CalleeSavedRegisters));
    }

    [Fact]
    public void ScalarAndSmallAggregateArguments_UseCanonicalRegistersThenAlignedStack()
    {
        HybridCpuAbiValueV2[] parameters = Enumerable.Range(0, 9)
            .Select(index => new HybridCpuAbiValueV2($"p{index}", HybridCpuAbiValueKindV2.Integer, 8, 8))
            .Append(new("pair", HybridCpuAbiValueKindV2.Aggregate, 16, 8))
            .ToArray();

        HybridCpuAbiLayoutV2 layout = Contract.Classify(new(parameters,
            new("result", HybridCpuAbiValueKindV2.Aggregate, 16, 8)));

        Assert.Equal(HybridCpuPlatformFactStatus.Supported, layout.Status);
        Assert.Equal([10, 11, 12, 13, 14, 15, 16, 17],
            layout.Parameters.Take(8).SelectMany(static location => location.Registers));
        Assert.Equal(HybridCpuAbiLocationKindV2.Stack, layout.Parameters[8].Kind);
        Assert.Equal(0, layout.Parameters[8].StackOffsetBytes);
        Assert.Equal(HybridCpuAbiLocationKindV2.Stack, layout.Parameters[9].Kind);
        Assert.Equal(8, layout.Parameters[9].StackOffsetBytes);
        Assert.Equal(32, layout.StackArgumentBytes);
        Assert.Equal(HybridCpuAbiLocationKindV2.RegisterPair, layout.ReturnValue!.Kind);
        Assert.Equal([10, 11], layout.ReturnValue.Registers);
    }

    [Fact]
    public void LargeReturn_IsExplicitIndirectResultInFirstArgumentRegister()
    {
        HybridCpuAbiLayoutV2 layout = Contract.Classify(new([], new(
            "large", HybridCpuAbiValueKindV2.Aggregate, 24, 8)));

        Assert.Equal(HybridCpuPlatformFactStatus.Supported, layout.Status);
        Assert.Equal(HybridCpuAbiLocationKindV2.Indirect, layout.ReturnValue!.Kind);
        Assert.Equal([10], layout.ReturnValue.Registers);
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void UnsupportedCallContours_FailClosed(bool varArgs, bool tailCall, bool special)
    {
        HybridCpuAbiLayoutV2 layout = Contract.Classify(new([], null, varArgs, tailCall, special));
        Assert.Equal(HybridCpuPlatformFactStatus.Unsupported, layout.Status);
        Assert.Empty(layout.Parameters);
    }

    [Fact]
    public void UnknownAndDuplicateSignatureFacts_FailClosed()
    {
        Assert.Equal(HybridCpuPlatformFactStatus.Unknown, Contract.Classify(new(
            [new("x", HybridCpuAbiValueKindV2.Unknown, 8, 8)], null)).Status);
        Assert.Equal(HybridCpuPlatformFactStatus.Invalid, Contract.Classify(new(
            [new("x", HybridCpuAbiValueKindV2.Integer, 8, 8),
             new("x", HybridCpuAbiValueKindV2.Integer, 8, 8)], null)).Status);
    }

    [Fact]
    public void FixedFrame_IsAlignedDeterministicAndHasExactAdjustments()
    {
        HybridCpuFrameRequestV2 firstRequest = new(
            [new("spill:z", 8, 8), new("spill:a", 4, 4)], [27, 8]);
        HybridCpuFrameRequestV2 secondRequest = new(
            [new("spill:a", 4, 4), new("spill:z", 8, 8)], [8, 27]);

        HybridCpuFrameLayoutV2 first = Contract.LayoutFrame(firstRequest);
        HybridCpuFrameLayoutV2 second = Contract.LayoutFrame(secondRequest);

        Assert.Equal(HybridCpuPlatformFactStatus.Supported, first.Status);
        Assert.Equal(32, first.FrameSizeBytes);
        Assert.Equal(-32, first.PrologueStackAdjustmentBytes);
        Assert.Equal(32, first.EpilogueStackAdjustmentBytes);
        Assert.Equal(0, first.FrameSizeBytes % HybridCpuNativeAbiContractV2.StackAlignmentBytes);
        Assert.Equal(first.Digest, second.Digest);
        Assert.Equal(first.Slots, second.Slots);
        Assert.Equal(["saved:x8", "saved:x27", "spill:a", "spill:z"],
            first.Slots.Select(static slot => slot.Identity));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void DynamicFramesAndProbing_AreExplicitlyUnsupported(bool dynamicAllocation, bool probing)
    {
        HybridCpuFrameLayoutV2 frame = Contract.LayoutFrame(new([], [], dynamicAllocation, probing));
        Assert.Equal(HybridCpuPlatformFactStatus.Unsupported, frame.Status);
        Assert.Equal(0, frame.FrameSizeBytes);
    }

    [Fact]
    public void InvalidSavedRegisterAndMalformedSlot_AreRejected()
    {
        Assert.Equal(HybridCpuPlatformFactStatus.Invalid,
            Contract.LayoutFrame(new([], [5])).Status);
        Assert.Equal(HybridCpuPlatformFactStatus.Unknown,
            Contract.LayoutFrame(new([new("spill", 0, 8)], [])).Status);
    }

    [Fact]
    public void ClassificationAndFrameBudgets_AreDeterministicAndFailClosed()
    {
        HybridCpuAbiValueV2[] parameters = Enumerable.Range(0, HybridCpuNativeAbiContractV2.MaximumParameters + 1)
            .Select(index => new HybridCpuAbiValueV2($"p{index}", HybridCpuAbiValueKindV2.Integer, 8, 8))
            .ToArray();
        Assert.Equal(HybridCpuPlatformFactStatus.Unsupported,
            Contract.Classify(new(parameters, null)).Status);
        Assert.Equal(HybridCpuPlatformFactStatus.Unsupported,
            Contract.LayoutFrame(new(
                [new("oversize", HybridCpuNativeAbiContractV2.MaximumValueSizeBytes + 1, 16)], [])).Status);
    }

    [Fact]
    public void Contract_ContainsNoRuntimeAllocationOrPublicationAuthority()
    {
        string[] names = typeof(HybridCpuNativeAbiContractV2).GetProperties()
            .Select(static property => property.Name).ToArray();
        Assert.DoesNotContain(names, static name => name.Contains("Rename", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, static name => name.Contains("FreeList", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, static name => name.Contains("Occupancy", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, static name => name.Contains("Commit", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, static name => name.Contains("Retire", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, static name => name.Contains("Publication", StringComparison.OrdinalIgnoreCase));
    }
}
