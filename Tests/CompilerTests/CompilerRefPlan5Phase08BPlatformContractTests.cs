using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.Target;
using Xunit;

namespace HybridCPU_ISE.Tests.CompilerTests;

public sealed class CompilerRefPlan5Phase08BPlatformContractTests
{
    private static HybridCpuTargetPlatformContractV1 Contract => HybridCpuTargetPlatformContractV1.Default;

    [Fact]
    public void Contract_IsVersionedDeterministicAndBoundToVerifiedCoreTarget()
    {
        Assert.Equal("hybridcpu.target-platform", HybridCpuTargetPlatformContractV1.SchemaId);
        Assert.Equal(1, HybridCpuTargetPlatformContractV1.SchemaMajor);
        Assert.Equal(HybridCpuTargetMachineContractV1.Default.ContractDigest, Contract.CoreTargetContractDigest);
        Assert.Equal("e2b369dc58e0100e72c58b43a386ab612c39e266f6cfb5f97160f6925b2481dd", Contract.ContractDigest);
        Assert.Equal(Contract.ContractDigest, HybridCpuTargetPlatformContractV1.Default.ContractDigest);
    }

    [Fact]
    public void NativeFunctionAndStackAbi_RemainExplicitlyUnsupportedWithoutHostDefaults()
    {
        Assert.Equal(HybridCpuPlatformFactStatus.Unsupported, Contract.CallingConvention.FullFunctionAbi);
        Assert.Empty(Contract.CallingConvention.ArgumentRegisters);
        Assert.Empty(Contract.CallingConvention.ReturnRegisters);
        Assert.Empty(Contract.CallingConvention.CallerSavedRegisters);
        Assert.Empty(Contract.CallingConvention.CalleeSavedRegisters);
        Assert.Equal(HybridCpuPlatformFactStatus.Unsupported, Contract.CallingConvention.AggregatePassing);
        Assert.Equal(HybridCpuPlatformFactStatus.Unsupported, Contract.CallingConvention.VarArgs);
        Assert.Equal(HybridCpuPlatformFactStatus.Unsupported, Contract.CallingConvention.TailCalls);
        Assert.Equal(HybridCpuPlatformFactStatus.Unsupported, Contract.CallingConvention.SpecialContourCalls);
        Assert.Equal(HybridCpuPlatformFactStatus.Unsupported, Contract.StackFrame.Support);
        Assert.Equal(0, Contract.StackFrame.StackAlignmentBytes);
        Assert.Null(Contract.StackFrame.StackPointerRegister);
        Assert.Null(Contract.StackFrame.FramePointerRegister);
        Assert.Equal(0, Contract.StackFrame.RedZoneBytes);
        Assert.Equal(HybridCpuPlatformFactStatus.Unsupported, Contract.StackFrame.DynamicAllocation);
        Assert.Equal(HybridCpuPlatformFactStatus.Unsupported, Contract.StackFrame.StackProbing);
    }

    [Fact]
    public void PlatformCompatibility_RejectsVersionAndDigestSkew()
    {
        Assert.Equal(HybridCpuPlatformCompatibility.Compatible, Contract.CheckCompatibility(
            1, Contract.CoreTargetContractDigest, HybridCpuTargetPlatformContractV1.MachineDescriptionDigest, Contract.ContractDigest));
        Assert.Equal(HybridCpuPlatformCompatibility.UnsupportedVersion, Contract.CheckCompatibility(
            2, Contract.CoreTargetContractDigest, HybridCpuTargetPlatformContractV1.MachineDescriptionDigest, Contract.ContractDigest));
        Assert.Equal(HybridCpuPlatformCompatibility.CoreTargetMismatch, Contract.CheckCompatibility(
            1, "stale-target", HybridCpuTargetPlatformContractV1.MachineDescriptionDigest, Contract.ContractDigest));
        Assert.Equal(HybridCpuPlatformCompatibility.MachineDescriptionMismatch, Contract.CheckCompatibility(
            1, Contract.CoreTargetContractDigest, "stale-machine", Contract.ContractDigest));
        Assert.Equal(HybridCpuPlatformCompatibility.PlatformContractMismatch, Contract.CheckCompatibility(
            1, Contract.CoreTargetContractDigest, HybridCpuTargetPlatformContractV1.MachineDescriptionDigest, "stale-platform"));
        Assert.Equal(HybridCpuPlatformCompatibility.Unknown, Contract.CheckCompatibility(1, "", "", ""));
    }

    [Fact]
    public void ExistingAggregateLayout_RemainsTheSingleTargetLayoutRule()
    {
        Assert.True(HybridCpuTargetMachineContractV1.Default.TryLayoutAggregate(
            [new("a", 1, 1), new("b", 8, 8), new("c", 2, 2)], out HybridCpuAggregateLayoutV1 layout));
        Assert.Equal(24, layout.SizeBytes);
        Assert.Equal(8, layout.AlignmentBytes);
        Assert.Equal([0, 8, 16], layout.Fields.Select(static field => field.OffsetBytes).ToArray());
        Assert.False(HybridCpuTargetMachineContractV1.Default.TryLayoutAggregate(
            [new("host-default-must-not-fill", 4, 16)], out _));
    }

    [Theory]
    [InlineData(32, 4)]
    [InlineData(64, 8)]
    [InlineData(64, 16)]
    public void AtomicWAndD_RequireNaturalAlignmentAndSequentialConsistency(int widthBits, int alignmentBytes)
    {
        Assert.Equal(HybridCpuPlatformFactStatus.Supported, Contract.ValidateAtomic(
            widthBits, alignmentBytes, IrMemoryOrdering.SequentiallyConsistent,
            IrAddressSpaceIdentity.Generic, isVolatile: false));
    }

    [Theory]
    [InlineData(IrMemoryOrdering.Acquire, true)]
    [InlineData(IrMemoryOrdering.Release, true)]
    [InlineData(IrMemoryOrdering.AcquireRelease, false)]
    [InlineData(IrMemoryOrdering.SequentiallyConsistent, false)]
    public void Phase12AtomicOrderingsAndVolatile_AreExplicitlySupported(IrMemoryOrdering ordering, bool isVolatile)
    {
        Assert.Equal(HybridCpuPlatformFactStatus.Supported,
            Contract.ValidateAtomic(64, 8, ordering, IrAddressSpaceIdentity.Generic, isVolatile));
    }

    [Theory]
    [InlineData(8, 1, IrMemoryOrdering.SequentiallyConsistent, IrAddressSpaceIdentity.Generic, false)]
    [InlineData(16, 2, IrMemoryOrdering.SequentiallyConsistent, IrAddressSpaceIdentity.Generic, false)]
    [InlineData(32, 2, IrMemoryOrdering.SequentiallyConsistent, IrAddressSpaceIdentity.Generic, false)]
    [InlineData(64, 8, IrMemoryOrdering.SequentiallyConsistent, IrAddressSpaceIdentity.Stack, false)]
    public void UnsupportedAtomicMatrix_FailsClosed(
        int widthBits,
        int alignmentBytes,
        IrMemoryOrdering ordering,
        IrAddressSpaceIdentity addressSpace,
        bool isVolatile)
    {
        Assert.Equal(HybridCpuPlatformFactStatus.Unsupported,
            Contract.ValidateAtomic(widthBits, alignmentBytes, ordering, addressSpace, isVolatile));
    }

    [Fact]
    public void UnknownAtomicInputs_RemainUnknown()
    {
        Assert.Equal(HybridCpuPlatformFactStatus.Unknown,
            Contract.ValidateAtomic(0, 0, IrMemoryOrdering.Unknown, IrAddressSpaceIdentity.Unknown, false));
        Assert.Equal(HybridCpuPlatformFactStatus.Supported, Contract.ValidateFence(IrMemoryOrdering.AcquireRelease));
        Assert.Equal(HybridCpuPlatformFactStatus.Supported, Contract.ValidateFence(IrMemoryOrdering.SequentiallyConsistent));
        Assert.Equal(HybridCpuPlatformFactStatus.Unknown, Contract.ValidateFence(IrMemoryOrdering.Unknown));
    }

    [Fact]
    public void TlsStartupHelpersAndObjectFormat_DoNotInheritHostCapabilities()
    {
        foreach (HybridCpuTlsModel model in Enum.GetValues<HybridCpuTlsModel>().Where(static item => item != HybridCpuTlsModel.Unknown))
            Assert.Equal(HybridCpuPlatformFactStatus.Unsupported, Contract.ValidateTls(model));
        Assert.Equal(HybridCpuPlatformFactStatus.Unknown, Contract.ValidateTls(HybridCpuTlsModel.Unknown));
        Assert.Equal(HybridCpuObjectFormat.None, Contract.ObjectFormat);
        Assert.Equal(HybridCpuPlatformFactStatus.Unsupported, Contract.RuntimeHelperAbi);
        Assert.Equal(HybridCpuPlatformFactStatus.Unsupported, Contract.ResolveRuntimeHelper("__hybridcpu_gc_alloc"));
        Assert.Equal(HybridCpuPlatformFactStatus.Invalid, Contract.ResolveRuntimeHelper(" "));
        Assert.Equal(HybridCpuPlatformFactStatus.Unsupported, Contract.ExecutableStartup);
        Assert.Equal(HybridCpuPlatformFactStatus.Unsupported, Contract.DynamicLibraries);
    }

    [Fact]
    public void ObjectWriterFeatureMatrix_IsIndependentlyVersionedAndFailClosed()
    {
        Assert.Equal(8, Contract.ObjectWriterFeatures.Count);
        Assert.All(Contract.ObjectWriterFeatures, static feature =>
        {
            Assert.Equal(1, feature.Version);
            Assert.Equal(HybridCpuPlatformFactStatus.Unsupported, feature.Support);
            Assert.False(string.IsNullOrWhiteSpace(feature.Identity));
        });
        Assert.Equal(Contract.ObjectWriterFeatures.Count,
            Contract.ObjectWriterFeatures.Select(static feature => feature.Identity).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void OnlyExistingInternalControlRelocation_IsSupported()
    {
        HybridCpuRelocationFeatureV1 supported = Assert.Single(
            Contract.RelocationFeatures, static feature => feature.Support == HybridCpuPlatformFactStatus.Supported);
        Assert.Equal(HybridCpuRelocationKind.BundleRelativeSigned16, supported.Kind);
        Assert.Equal(HybridCpuRelocationOwner.CompilerInternalControlFlow, supported.Owner);
        Assert.False(supported.AllowsAddend);
        Assert.Equal(HybridCpuPlatformFactStatus.Supported, Contract.ValidateInternalControlRelocation(short.MinValue, 0));
        Assert.Equal(HybridCpuPlatformFactStatus.Supported, Contract.ValidateInternalControlRelocation(short.MaxValue, 0));
        Assert.Equal(HybridCpuPlatformFactStatus.Invalid, Contract.ValidateInternalControlRelocation(short.MaxValue + 1L, 0));
        Assert.Equal(HybridCpuPlatformFactStatus.Invalid, Contract.ValidateInternalControlRelocation(0, 1));
    }

    [Fact]
    public void ObjectMetadataOrdering_IsDeterministicUnderInputPermutation()
    {
        HybridCpuObjectSectionRequestV1[] sections = [new(".data", HybridCpuObjectSectionKind.WritableData, 8), new(".text", HybridCpuObjectSectionKind.Code, 32)];
        HybridCpuObjectSymbolRequestV1[] symbols =
        [
            new("z", HybridCpuSymbolBinding.Global, HybridCpuSymbolVisibility.Default, ".text", true),
            new("a", HybridCpuSymbolBinding.Local, HybridCpuSymbolVisibility.Hidden, ".data", true)
        ];
        HybridCpuObjectRelocationRequestV1[] relocations =
        [
            new(".text", 32, HybridCpuRelocationKind.BundleRelativeSigned16, "z", 0),
            new(".text", 0, HybridCpuRelocationKind.BundleRelativeSigned16, "a", 0)
        ];

        HybridCpuObjectMetadataPlanV1 first = Contract.PlanObjectMetadata(sections, symbols, relocations);
        HybridCpuObjectMetadataPlanV1 second = Contract.PlanObjectMetadata(
            sections.Reverse().ToArray(), symbols.Reverse().ToArray(), relocations.Reverse().ToArray());
        Assert.Equal(HybridCpuPlatformFactStatus.Unsupported, first.Status);
        Assert.Equal(first.Digest, second.Digest);
        Assert.Equal(first.Sections, second.Sections);
        Assert.Equal(first.Symbols, second.Symbols);
        Assert.Equal(first.Relocations, second.Relocations);
    }

    [Fact]
    public void MalformedObjectMetadata_IsRejectedBeforeUnsupportedWriterStatus()
    {
        HybridCpuObjectMetadataPlanV1 duplicateSection = Contract.PlanObjectMetadata(
            [new(".text", HybridCpuObjectSectionKind.Code, 32), new(".text", HybridCpuObjectSectionKind.Code, 32)], [], []);
        HybridCpuObjectMetadataPlanV1 missingSection = Contract.PlanObjectMetadata(
            [], [new("f", HybridCpuSymbolBinding.Global, HybridCpuSymbolVisibility.Default, ".text", true)], []);
        HybridCpuObjectMetadataPlanV1 unresolvedRelocation = Contract.PlanObjectMetadata(
            [new(".text", HybridCpuObjectSectionKind.Code, 32)], [],
            [new(".text", 0, HybridCpuRelocationKind.BundleRelativeSigned16, "missing", 0)]);

        Assert.Equal(HybridCpuPlatformFactStatus.Invalid, duplicateSection.Status);
        Assert.Equal(HybridCpuPlatformFactStatus.Invalid, missingSection.Status);
        Assert.Equal(HybridCpuPlatformFactStatus.Invalid, unresolvedRelocation.Status);
    }

    [Fact]
    public void DuplicateAndComdatPolicies_AreExplicit()
    {
        HybridCpuObjectSectionRequestV1[] sections = [new(".text", HybridCpuObjectSectionKind.Code, 32)];
        HybridCpuObjectSymbolRequestV1 definition =
            new("f", HybridCpuSymbolBinding.Global, HybridCpuSymbolVisibility.Default, ".text", true);
        HybridCpuObjectMetadataPlanV1 duplicate = Contract.PlanObjectMetadata(sections, [definition, definition], []);
        HybridCpuObjectMetadataPlanV1 comdat = Contract.PlanObjectMetadata(
            sections, [definition with { ComdatKey = "f" }, definition with { ComdatKey = "f" }], []);
        HybridCpuObjectMetadataPlanV1 declarationAndDefinition = Contract.PlanObjectMetadata(
            sections, [definition, definition with { IsDefinition = false, SectionName = null }], []);

        Assert.Equal(HybridCpuPlatformFactStatus.Invalid, duplicate.Status);
        Assert.Equal(HybridCpuPlatformFactStatus.Unsupported, comdat.Status);
        Assert.Equal(HybridCpuPlatformFactStatus.Unsupported, declarationAndDefinition.Status);
    }

    [Fact]
    public void UnsupportedObjectRelocations_AreRejectedWithoutApproximation()
    {
        HybridCpuObjectSectionRequestV1[] sections = [new(".text", HybridCpuObjectSectionKind.Code, 32)];
        HybridCpuObjectSymbolRequestV1[] symbols =
            [new("target", HybridCpuSymbolBinding.Global, HybridCpuSymbolVisibility.Default, ".text", true)];
        foreach (HybridCpuRelocationKind kind in new[]
                 { HybridCpuRelocationKind.Absolute64, HybridCpuRelocationKind.PcRelative32, HybridCpuRelocationKind.ThreadLocal })
        {
            HybridCpuObjectMetadataPlanV1 plan = Contract.PlanObjectMetadata(
                sections, symbols, [new(".text", 0, kind, "target", 4)]);
            Assert.Equal(HybridCpuPlatformFactStatus.Unsupported, plan.Status);
        }
        Assert.Equal(HybridCpuPlatformFactStatus.Invalid, Contract.PlanObjectMetadata(
            sections, symbols, [new(".text", 1, HybridCpuRelocationKind.BundleRelativeSigned16, "target", 0)]).Status);
        Assert.Equal(HybridCpuPlatformFactStatus.Invalid, Contract.PlanObjectMetadata(
            sections, symbols, [new(".text", 0, HybridCpuRelocationKind.BundleRelativeSigned16, "target", 1)]).Status);
    }

    [Fact]
    public void NativeOriginMap_RoundTripsInDeterministicInstructionOrder()
    {
        IrSourceSpan span = new("kernel.hasm", 2, 3, 2, 9, 10, 6);
        HybridCpuDebugOriginRecordV1[] records =
        [
            new(8, IrSourceOriginChainV1.Native(8, 0x100, null)),
            new(2, IrSourceOriginChainV1.Native(2, 0x40, span))
        ];
        HybridCpuDebugMapResultV1 first = Contract.BuildNativeDebugMap(records);
        HybridCpuDebugMapResultV1 second = Contract.BuildNativeDebugMap(records.Reverse().ToArray());

        Assert.Equal(HybridCpuPlatformFactStatus.Supported, first.Status);
        Assert.Equal([2, 8], first.Records.Select(static record => record.InstructionIndex).ToArray());
        Assert.Equal(first.Records, second.Records);
        Assert.Equal(first.Digest, second.Digest);
        Assert.Equal(HybridCpuPlatformFactStatus.Unsupported, Contract.ObjectDebugSections);
    }

    [Fact]
    public void UnknownOrMalformedOrigins_FailClosed()
    {
        IrSourceOriginChainV1 unknown = new(1,
            [new("unknown", IrSourceOriginKind.Llvm, "frontend", "1", null, IrFrontendEvidenceTrust.Unknown)]);
        Assert.Equal(HybridCpuPlatformFactStatus.Unknown,
            Contract.BuildNativeDebugMap([new(0, unknown)]).Status);
        Assert.Equal(HybridCpuPlatformFactStatus.Invalid,
            Contract.BuildNativeDebugMap([new(0, IrSourceOriginChainV1.Native(0, 0, null)), new(0, IrSourceOriginChainV1.Native(0, 0, null))]).Status);
        Assert.Equal(HybridCpuPlatformFactStatus.Unsupported, Contract.UnwindSemantics);
        Assert.Equal(HybridCpuPlatformFactStatus.Unsupported, Contract.ExceptionHandlingSemantics);
    }

    [Fact]
    public void PlatformContract_ExposesNoRuntimeAllocationOrAuthorityState()
    {
        string[] propertyNames = typeof(HybridCpuTargetPlatformContractV1).GetProperties()
            .Select(static property => property.Name).ToArray();
        Assert.DoesNotContain(propertyNames, static name => name.Contains("Rename", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(propertyNames, static name => name.Contains("Occupancy", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(propertyNames, static name => name.Contains("Scoreboard", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(propertyNames, static name => name.Contains("Commit", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(propertyNames, static name => name.Contains("Retire", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(propertyNames, static name => name.Contains("Publication", StringComparison.OrdinalIgnoreCase));
    }
}
