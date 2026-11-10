using System.Buffers.Binary;
using System.Reflection;
using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.IR.Contracts;
using HybridCPU.Compiler.Core.Target;
using HybridCPU.Compiler.Core.Target.Managed;

namespace HybridCPU_ISE.Tests.CompilerTests;

public sealed class CompilerRefPlan5Phase22ManagedAbiTests
{
    private static HybridCpuManagedAbiFamilyV1 Family => HybridCpuManagedAbiFamilyV1.Default;

    [Fact]
    public void Family_IsVersionedTargetBoundAndDeterministic()
    {
        Assert.Equal("hybridcpu.managed-abi-family", HybridCpuManagedAbiFamilyV1.SchemaId);
        Assert.Equal((1, 63), (HybridCpuManagedAbiFamilyV1.SchemaMajor, HybridCpuManagedAbiFamilyV1.SchemaMinor));
        Assert.Equal(HybridCpuTargetMachineContractV1.Default.ContractDigest, Family.TargetContractDigest);
        Assert.Equal(HybridCpuTargetMachineContractV1.DataLayoutVersion, Family.TargetDataLayoutVersion);
        Assert.Equal(HybridCpuNativeAbiContractV2.Default.ContractDigest, Family.NativeAbiDigest);
        Assert.Equal(64, Family.ReferenceBitWidth);
        Assert.Equal(8, Family.ReferenceAlignmentBytes);
        Assert.Equal(0UL, Family.NullReferenceValue);
        Assert.Equal(Family.ContractDigest, HybridCpuManagedAbiFamilyV1.Default.ContractDigest);
        Assert.Equal(64, Family.ContractDigest.Length);
    }

    [Fact]
    public void Subcontracts_AreIndependentAndDoNotImplyUmbrellaRuntimeSupport()
    {
        Assert.Equal(11, Family.Subcontracts.Count);
        Assert.Equal(11, Family.Subcontracts.Values.Select(static contract => contract.SchemaId).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(11, Family.Subcontracts.Values.Select(static contract => contract.Digest).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(HybridCpuManagedAbiSupportV1.Supported, Family.Subcontracts["ManagedCallAbi"].Support);
        Assert.Equal(HybridCpuManagedAbiSupportV1.Supported, Family.Subcontracts["ManagedReferenceAbi"].Support);
        Assert.Equal(HybridCpuManagedAbiSupportV1.Supported, Family.Subcontracts["GcInfoAbi"].Support);
        Assert.Equal(HybridCpuManagedAbiSupportV1.Supported, Family.Subcontracts["SafepointAbi"].Support);
        Assert.Equal(HybridCpuManagedAbiSupportV1.ContractOnly, Family.Subcontracts["RuntimeHelperAbi"].Support);
        Assert.Equal(HybridCpuManagedAbiSupportV1.Supported, Family.Subcontracts["TransitionThunkAbi"].Support);
        Assert.Equal(HybridCpuManagedAbiSupportV1.Supported, Family.Subcontracts["EhUnwindAbi"].Support);
        Assert.Equal(HybridCpuManagedAbiSupportV1.Supported, Family.Subcontracts["TrapMappingAbi"].Support);
        Assert.Equal(HybridCpuManagedAbiSupportV1.Supported, Family.Subcontracts["CodeManagerMetadataAbi"].Support);
        Assert.Equal(HybridCpuManagedAbiSupportV1.Supported, Family.Subcontracts["PlatformBootstrapAbi"].Support);
    }

    [Theory]
    [InlineData("ManagedCallAbi")]
    [InlineData("ManagedReferenceAbi")]
    [InlineData("GcInfoAbi")]
    [InlineData("SafepointAbi")]
    [InlineData("RuntimeHelperAbi")]
    [InlineData("ThreadContextAbi")]
    [InlineData("TransitionThunkAbi")]
    [InlineData("EhUnwindAbi")]
    [InlineData("TrapMappingAbi")]
    [InlineData("CodeManagerMetadataAbi")]
    [InlineData("PlatformBootstrapAbi")]
    public void EachSubcontract_HasIndependentCompatibilityIdentity(string identity)
    {
        HybridCpuManagedAbiSubcontractV1 contract = Family.Subcontracts[identity];
        Assert.Equal((1, 0), (contract.SchemaMajor, contract.SchemaMinor));
        Assert.StartsWith("hybridcpu.managed-abi/", contract.SchemaId, StringComparison.Ordinal);
        Assert.Equal(64, contract.Digest.Length);
        Assert.False(string.IsNullOrWhiteSpace(contract.Scope));
    }

    [Fact]
    public void RestrictedValueCall_ReusesNativeAbiV2Exactly()
    {
        HybridCpuManagedCallLayoutV1 managed = Family.ClassifyCall(new(
            HybridCpuManagedCallDirectionV1.ManagedToManaged,
            [Value("a", 8), Value("b", 8), Value("pair", 16, HybridCpuManagedValueKindV1.BlittableValue)],
            Value("result", 8)));
        HybridCpuAbiLayoutV2 native = HybridCpuNativeAbiContractV2.Default.Classify(new(
            [
                new("a", HybridCpuAbiValueKindV2.Integer, 8, 8),
                new("b", HybridCpuAbiValueKindV2.Integer, 8, 8),
                new("pair", HybridCpuAbiValueKindV2.Aggregate, 16, 8)
            ],
            new("result", HybridCpuAbiValueKindV2.Integer, 8, 8)));

        Assert.Equal(HybridCpuPlatformFactStatus.Supported, managed.Status);
        Assert.Equal(native.Digest, managed.NativeLayout!.Digest);
        Assert.Equal(native.StackArgumentBytes, managed.NativeLayout.StackArgumentBytes);
        Assert.Equal(native.Parameters.Select(LocationProjection), managed.NativeLayout.Parameters.Select(LocationProjection));
        Assert.Equal(LocationProjection(native.ReturnValue!), LocationProjection(managed.NativeLayout.ReturnValue!));
        Assert.Equal([10, 11, 12, 13], managed.NativeLayout!.Parameters.SelectMany(static location => location.Registers));
        Assert.Equal([10], managed.NativeLayout.ReturnValue!.Registers);
    }

    [Fact]
    public void CallFrameEncoding_IsGoldenDeterministicAndFrontendIndependent()
    {
        HybridCpuManagedCallLayoutV1 first = Family.ClassifyCall(new(
            HybridCpuManagedCallDirectionV1.ManagedToManaged,
            [Value("a", 8), Value("b", 8)], Value("result", 8)));
        HybridCpuManagedCallLayoutV1 second = Family.ClassifyCall(new(
            HybridCpuManagedCallDirectionV1.ManagedToManaged,
            [Value("a", 8), Value("b", 8)], Value("result", 8)));

        byte[] firstBytes = HybridCpuManagedAbiEncodingV1.EncodeCallFrame(first);
        byte[] secondBytes = HybridCpuManagedAbiEncodingV1.EncodeCallFrame(second);
        Assert.Equal(firstBytes, secondBytes);
        Assert.Equal("48434d4301003f0002000000010000000000000000010a00ffffffff0800000000010b00ffffffff0800000000010a00ffffffff08000000",
            Convert.ToHexString(firstBytes).ToLowerInvariant());
    }

    [Theory]
    [InlineData(HybridCpuManagedValueKindV1.ManagedByRef)]
    [InlineData(HybridCpuManagedValueKindV1.InteriorReference)]
    public void ByRefCalls_FailClosed(HybridCpuManagedValueKindV1 kind)
    {
        HybridCpuManagedCallLayoutV1 layout = Family.ClassifyCall(new(
            HybridCpuManagedCallDirectionV1.ManagedToManaged,
            [Value("value", 8, kind)], null));

        Assert.Equal(HybridCpuPlatformFactStatus.Unsupported, layout.Status);
        Assert.Null(layout.NativeLayout);
    }

    [Theory]
    [InlineData(HybridCpuManagedCallDirectionV1.ManagedToUnmanaged, HybridCpuPlatformFactStatus.Supported)]
    [InlineData(HybridCpuManagedCallDirectionV1.UnmanagedToManaged, HybridCpuPlatformFactStatus.Unsupported)]
    public void TransitionCalls_AreIndependentlyClassified(HybridCpuManagedCallDirectionV1 direction,
        HybridCpuPlatformFactStatus expected)
    {
        HybridCpuManagedCallLayoutV1 layout = Family.ClassifyCall(new(direction, [Value("x", 8)], null));
        Assert.Equal(expected, layout.Status);
        Assert.Equal(HybridCpuManagedAbiSupportV1.Supported, Family.Subcontracts["TransitionThunkAbi"].Support);
    }

    [Fact]
    public void GcReferenceMap_IsGoldenDeterministicAcrossInputOrdering()
    {
        HybridCpuSafepointRecordV1 firstRecord = Safepoint(32, HybridCpuSafepointCategoryV1.CallSite,
            StackRef("stack:z", 16), RegisterRef("reg:a", 10));
        HybridCpuSafepointRecordV1 secondRecord = Safepoint(64, HybridCpuSafepointCategoryV1.PollSite,
            RegisterRef("reg:b", 11));

        HybridCpuGcInfoEncodingResultV1 first = HybridCpuManagedAbiEncodingV1.EncodeGcInfo([secondRecord, firstRecord]);
        HybridCpuGcInfoEncodingResultV1 second = HybridCpuManagedAbiEncodingV1.EncodeGcInfo([firstRecord, secondRecord]);

        Assert.Equal(HybridCpuPlatformFactStatus.Supported, first.Status);
        Assert.Equal(first.Bytes, second.Bytes);
        Assert.Equal(first.Digest, second.Digest);
        Assert.Equal("48434d4701000000020000002000000000000200e0074438be9ae85100000a00ffffffff256fd3d44dfa8e670001ffff1000000040000000010001008f9b3eb05f1d592b00000b00ffffffff",
            Convert.ToHexString(first.Bytes).ToLowerInvariant());
    }

    [Fact]
    public void ReferenceMapCoverage_RequiresEveryLiveRegisterAndStackReference()
    {
        HybridCpuSafepointRecordV1 record = Safepoint(8, HybridCpuSafepointCategoryV1.CallSite,
            RegisterRef("r", 18), StackRef("s", 24));

        Assert.Equal(HybridCpuPlatformFactStatus.Supported,
            HybridCpuManagedAbiEncodingV1.ValidateReferenceCoverage(record, ["s", "r"]));
        Assert.Equal(HybridCpuPlatformFactStatus.Unsupported,
            HybridCpuManagedAbiEncodingV1.ValidateReferenceCoverage(record, ["r", "s", "missing"]));
        Assert.Equal(HybridCpuPlatformFactStatus.Invalid,
            HybridCpuManagedAbiEncodingV1.ValidateReferenceCoverage(record, ["r", "r"]));
    }

    [Theory]
    [InlineData(HybridCpuGcReferenceKindV1.ManagedByRef)]
    [InlineData(HybridCpuGcReferenceKindV1.InteriorReference)]
    public void ByRefAndInteriorReferenceMaps_AreExplicitlyUnsupported(HybridCpuGcReferenceKindV1 kind)
    {
        HybridCpuGcInfoEncodingResultV1 result = HybridCpuManagedAbiEncodingV1.EncodeGcInfo([
            Safepoint(0, HybridCpuSafepointCategoryV1.CallSite,
                new HybridCpuGcReferenceLocationV1("ref", kind, HybridCpuGcLocationKindV1.Register, 10, null))
        ]);

        Assert.Equal(HybridCpuPlatformFactStatus.Unsupported, result.Status);
        Assert.Empty(result.Bytes);
    }

    [Theory]
    [InlineData(-1, null)]
    [InlineData(32, null)]
    [InlineData(null, 3)]
    public void MalformedOrUnalignedReferenceLocations_AreInvalid(int? register, int? stackOffset)
    {
        HybridCpuGcLocationKindV1 locationKind = register.HasValue
            ? HybridCpuGcLocationKindV1.Register : HybridCpuGcLocationKindV1.Stack;
        HybridCpuGcInfoEncodingResultV1 result = HybridCpuManagedAbiEncodingV1.EncodeGcInfo([
            Safepoint(0, HybridCpuSafepointCategoryV1.CallSite,
                new HybridCpuGcReferenceLocationV1("ref", HybridCpuGcReferenceKindV1.ObjectReference, locationKind, register, stackOffset))
        ]);

        Assert.Equal(HybridCpuPlatformFactStatus.Invalid, result.Status);
    }

    [Fact]
    public void RuntimeConsumer_RejectsVersionTargetNativeAbiAndPackSkewBeforeMetadataUse()
    {
        HybridCpuManagedAbiConsumerV1 consumer = new HybridCpuManagedAbiConsumerV1();
        HybridCpuManagedAbiEnvelopeV1 valid = Family.CreateEnvelope(["managed.direct-value-call"]);

        Assert.Equal(HybridCpuManagedAbiCompatibilityV1.Compatible, consumer.CheckCompatibility(valid));
        Assert.Equal(HybridCpuManagedAbiCompatibilityV1.UnsupportedMajorVersion,
            consumer.CheckCompatibility(valid with { FamilyMajor = 2 }));
        Assert.Equal(HybridCpuManagedAbiCompatibilityV1.UnsupportedMinorVersion,
            consumer.CheckCompatibility(valid with { FamilyMinor = HybridCpuManagedAbiFamilyV1.SchemaMinor + 1 }));
        Assert.Equal(HybridCpuManagedAbiCompatibilityV1.TargetMismatch,
            consumer.CheckCompatibility(valid with { TargetContractDigest = new string('a', 64) }));
        Assert.Equal(HybridCpuManagedAbiCompatibilityV1.NativeAbiMismatch,
            consumer.CheckCompatibility(valid with { NativeAbiDigest = new string('b', 64) }));
        Assert.Equal(HybridCpuManagedAbiCompatibilityV1.RuntimePackMismatch,
            consumer.CheckCompatibility(valid with { RuntimePackRevision = "host-default" }));
    }

    [Fact]
    public void RuntimeConsumer_RejectsSubcontractSkewAndUnsupportedFeatureClaims()
    {
        HybridCpuManagedAbiConsumerV1 consumer = new HybridCpuManagedAbiConsumerV1();
        HybridCpuManagedAbiEnvelopeV1 valid = Family.CreateEnvelope();
        Dictionary<string, string> skewed = valid.SubcontractDigests.ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal);
        skewed["GcInfoAbi"] = new string('c', 64);

        Assert.Equal(HybridCpuManagedAbiCompatibilityV1.SubcontractMismatch,
            consumer.CheckCompatibility(valid with { SubcontractDigests = skewed }));
        Assert.Equal(HybridCpuManagedAbiCompatibilityV1.Compatible,
            consumer.CheckCompatibility(Family.CreateEnvelope(["managed.object-reference"])));
        Assert.Equal(HybridCpuManagedAbiCompatibilityV1.MissingRequiredSupport,
            consumer.CheckCompatibility(Family.CreateEnvelope(["unknown.feature"])));
    }

    [Fact]
    public void GcMetadataHeader_IsValidatedOnlyAfterEnvelopeCompatibility()
    {
        HybridCpuGcInfoEncodingResultV1 gc = HybridCpuManagedAbiEncodingV1.EncodeGcInfo([
            Safepoint(0, HybridCpuSafepointCategoryV1.CallSite, RegisterRef("r", 10))
        ]);
        HybridCpuManagedAbiConsumerV1 consumer = new HybridCpuManagedAbiConsumerV1();
        HybridCpuManagedAbiEnvelopeV1 envelope = Family.CreateEnvelope();

        Assert.Equal(HybridCpuManagedAbiCompatibilityV1.Compatible,
            consumer.ValidateGcInfoHeader(envelope, gc.Bytes));
        byte[] unknownMajor = (byte[])gc.Bytes.Clone();
        BinaryPrimitives.WriteUInt16LittleEndian(unknownMajor.AsSpan(4), 2);
        Assert.Equal(HybridCpuManagedAbiCompatibilityV1.UnsupportedMajorVersion,
            consumer.ValidateGcInfoHeader(envelope, unknownMajor));
        Assert.Equal(HybridCpuManagedAbiCompatibilityV1.UnsupportedMajorVersion,
            consumer.ValidateGcInfoHeader(envelope with { FamilyMajor = 2 }, Array.Empty<byte>()));
        byte[] trailing = [.. gc.Bytes, 0];
        Assert.Equal(HybridCpuManagedAbiCompatibilityV1.InvalidMetadata,
            consumer.ValidateGcInfoHeader(envelope, trailing));
        byte[] corruptRegister = (byte[])gc.Bytes.Clone();
        BinaryPrimitives.WriteInt16LittleEndian(corruptRegister.AsSpan(30), 32);
        Assert.Equal(HybridCpuManagedAbiCompatibilityV1.InvalidMetadata,
            consumer.ValidateGcInfoHeader(envelope, corruptRegister));
    }

    [Fact]
    public void RuntimeHelperTable_QualifiesOnlyVersionedExactHelpers()
    {
        Assert.Equal(Family.RuntimeHelpers.Count,
            Family.RuntimeHelpers.Select(static helper => helper.Symbol).Distinct(StringComparer.Ordinal).Count());
        HybridCpuRuntimeHelperV1 bootstrap = Assert.IsType<HybridCpuRuntimeHelperV1>(
            Family.ResolveRuntimeHelper("__hybridcpu_runtime_bootstrap"));
        Assert.Equal("void(execution-context:nuint)", bootstrap.Signature);
        Assert.Equal(HybridCpuManagedAbiSupportV1.Supported, bootstrap.Support);
        Assert.Equal(HybridCpuManagedAbiSupportV1.Supported,
            Family.ResolveRuntimeHelper("__hybridcpu_managed_null_check")!.Support);
        Assert.All(Family.RuntimeHelpers.Where(static helper => helper.Support != HybridCpuManagedAbiSupportV1.Supported), static helper =>
        {
            Assert.StartsWith("__hybridcpu_managed_", helper.Symbol, StringComparison.Ordinal);
            Assert.False(string.IsNullOrWhiteSpace(helper.Signature));
            Assert.False(string.IsNullOrWhiteSpace(helper.CallingConvention));
            Assert.NotEqual(HybridCpuRuntimeHelperGcTransitionV1.Unknown, helper.GcTransition);
            Assert.Equal(HybridCpuManagedAbiSupportV1.Unsupported, helper.Support);
        });
        Assert.Equal(IrMemoryEffectKind.Read | IrMemoryEffectKind.Write,
            Family.ResolveRuntimeHelper("__hybridcpu_managed_write_barrier")!.MemoryEffects);
        Assert.Null(Family.ResolveRuntimeHelper("host_gc_alloc"));
    }

    [Fact]
    public void ThreadContextTlsEhAndPinning_AreQualifiedWhileTransitionsRemainUnavailable()
    {
        Assert.Equal(HybridCpuNativeAbiContractV2.ThreadPointerRegister, Family.ThreadPointerRegister);
        Assert.Equal(HybridCpuManagedAbiSupportV1.Supported, Family.Subcontracts["ThreadContextAbi"].Support);
        Assert.Contains(Family.Features, static feature => feature.Identity == "managed.tls" && feature.Support == HybridCpuManagedAbiSupportV1.Supported);
        Assert.Contains(Family.Features, static feature => feature.Identity == "managed.threading-rendezvous" && feature.Support == HybridCpuManagedAbiSupportV1.Supported);
        Assert.Contains(Family.Features, static feature => feature.Identity == "managed.synchronization-v1" && feature.Support == HybridCpuManagedAbiSupportV1.Supported);
        Assert.Contains(Family.Features, static feature => feature.Identity == "managed.eh-unwind" && feature.Support == HybridCpuManagedAbiSupportV1.Supported);
        Assert.Contains(Family.Features, static feature => feature.Identity == "managed.pinning-handles" && feature.Support == HybridCpuManagedAbiSupportV1.Supported);
        Assert.Equal(HybridCpuNativeAbiContractV2.StackPointerRegister, Family.RequiredSafepointState.StackPointerRegister);
        Assert.Equal(HybridCpuNativeAbiContractV2.FramePointerRegister, Family.RequiredSafepointState.FramePointerRegister);
        Assert.Equal(HybridCpuNativeAbiContractV2.ReturnAddressRegister, Family.RequiredSafepointState.ReturnAddressRegister);
        Assert.Equal(HybridCpuNativeAbiContractV2.Default.CalleeSavedRegisters, Family.RequiredSafepointState.PreservedRegisters);
        Assert.True(Family.RequiredSafepointState.RequiresInstructionPointer);
        Assert.True(Family.RequiredSafepointState.RequiresExactFrameSize);
    }

    [Fact]
    public void CodeManagerMetadata_IsGoldenSortedAndClaimsOnlyBootstrapRegistration()
    {
        string gcA = new string('a', 64);
        string gcB = new string('b', 64);
        byte[] first = HybridCpuManagedAbiEncodingV1.EncodeCodeManagerMetadata([
            new("m2", 64, 32, gcB, null), new("m1", 0, 64, gcA, null)
        ]);
        byte[] second = HybridCpuManagedAbiEncodingV1.EncodeCodeManagerMetadata([
            new("m1", 0, 64, gcA, null), new("m2", 64, 32, gcB, null)
        ]);

        Assert.Equal(first, second);
        Assert.Equal(172, first.Length);
        Assert.Equal(HybridCpuManagedAbiSupportV1.Supported, Family.Subcontracts["CodeManagerMetadataAbi"].Support);
        Assert.Contains(Family.Features, static feature =>
            feature.Identity == "managed.code-manager-registration" && feature.Support == HybridCpuManagedAbiSupportV1.Supported);
    }

    [Fact]
    public void ManagedMetadataSurface_HasNoRuntimeExecutionOrPublicationAuthority()
    {
        Assert.False(Family.HasRuntimeAuthority);
        Assert.False(Family.HasObjectLifetimeAuthority);
        Assert.False(Family.HasGcPhaseAuthority);
        Assert.False(Family.HasThreadSuspensionAuthority);
        Assert.False(Family.HasExceptionDispatchAuthority);
        Assert.False(Family.HasPublicationAuthority);
        string[] propertyNames = typeof(HybridCpuManagedAbiFamilyV1).GetProperties().Select(static property => property.Name).ToArray();
        Assert.DoesNotContain(propertyNames, static name => name.Contains("Permission", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(propertyNames, static name => name.Contains("Commit", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(propertyNames, static name => name.Contains("Retire", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(propertyNames, static name => name.Contains("Epoch", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ReceiverLoan_RequiresExactCallerStorageProofAndDoesNotOpenGeneralByRef()
    {
        HybridCpuManagedCallSignatureV1 signature = new HybridCpuManagedCallSignatureV1(
            HybridCpuManagedCallDirectionV1.ManagedToManaged,
            [new("[Fixture]Point&", HybridCpuManagedValueKindV1.ManagedByRef, 8, 8), Value("delta", 4)],
            Value("result", 4));
        string importer = new('a', 64);
        string storage = new('b', 64);
        HybridCpuManagedReceiverLoanCallV1 loan = new HybridCpuManagedReceiverLoanCallV1(signature, 0, "[Fixture]Point", 16, 8,
            16, 8, true, true, true, true, true, importer, storage);

        HybridCpuManagedCallLayoutV1 admitted = Family.ClassifyReceiverLoan(loan);
        Assert.Equal(HybridCpuPlatformFactStatus.Supported, admitted.Status);
        HybridCpuAbiLocationV2 receiver = Assert.Single(admitted.NativeLayout!.Parameters,
            static location => location.ValueIdentity == "[Fixture]Point&");
        Assert.Equal(HybridCpuAbiLocationKindV2.Register, receiver.Kind);
        Assert.Equal([10], receiver.Registers);
        Assert.Equal(admitted.Digest, Family.ClassifyReceiverLoan(loan).Digest);

        Assert.Equal(HybridCpuPlatformFactStatus.Unsupported, Family.ClassifyCall(signature).Status);
        Assert.Equal(HybridCpuPlatformFactStatus.Unsupported,
            Family.ClassifyReceiverLoan(loan with { ScopedTypeIdentity = "[Fixture]Other" }).Status);
        Assert.Equal(HybridCpuPlatformFactStatus.Unsupported,
            Family.ClassifyReceiverLoan(loan with { CallerStorageSizeBytes = 8 }).Status);
        Assert.Equal(HybridCpuPlatformFactStatus.Unsupported,
            Family.ClassifyReceiverLoan(loan with { CallerStorageBoundsProven = false }).Status);
        Assert.Equal(HybridCpuPlatformFactStatus.Unsupported,
            Family.ClassifyReceiverLoan(loan with { NoSafepoints = false }).Status);
        Assert.Equal(HybridCpuPlatformFactStatus.Unsupported,
            Family.ClassifyReceiverLoan(loan with { ReferenceFree = false }).Status);
        Assert.Equal(HybridCpuPlatformFactStatus.Unsupported,
            Family.ClassifyReceiverLoan(loan with { CallerStorageProofDigest = "manual" }).Status);
        HybridCpuManagedCallSignatureV1 secondByRef = signature with { Parameters = [signature.Parameters[0],
            new("other&", HybridCpuManagedValueKindV1.ManagedByRef, 8, 8)] };
        Assert.Equal(HybridCpuPlatformFactStatus.Unsupported,
            Family.ClassifyReceiverLoan(loan with { Signature = secondByRef }).Status);
    }

    [Fact]
    public void ContractExistence_DoesNotEnableDotNetFrontendOrManagedSafety()
    {
        CompilerFeatureSet native = CompilerCrossLayerSchemaCatalogV1.NativeV1;
        Assert.Contains(native.FrontendFeatures, static feature =>
            feature.Id == "frontend.dotnet" && feature.Support == CompilerFeatureSupport.Unsupported);
        Assert.Equal(CompilerManagedSafetyLevel.UnmanagedOnly, native.ManagedSafetyLevel);
        Assert.Contains(Family.Features, static feature =>
            feature.Identity == "managed.object-reference" && feature.Support == HybridCpuManagedAbiSupportV1.Supported);
        Assert.DoesNotContain(Family.Features, static feature =>
            feature.Identity == "managed.runtime-helpers" && feature.Support == HybridCpuManagedAbiSupportV1.Supported);
    }

    private static HybridCpuManagedValueV1 Value(
        string identity,
        int size,
        HybridCpuManagedValueKindV1 kind = HybridCpuManagedValueKindV1.PrimitiveInteger) =>
        new(identity, kind, size, Math.Min(8, size));

    private static HybridCpuGcReferenceLocationV1 RegisterRef(string identity, int register) =>
        new(identity, HybridCpuGcReferenceKindV1.ObjectReference, HybridCpuGcLocationKindV1.Register, register, null);

    private static HybridCpuGcReferenceLocationV1 StackRef(string identity, int offset) =>
        new(identity, HybridCpuGcReferenceKindV1.ObjectReference, HybridCpuGcLocationKindV1.Stack, null, offset);

    private static HybridCpuSafepointRecordV1 Safepoint(
        int offset,
        HybridCpuSafepointCategoryV1 category,
        params HybridCpuGcReferenceLocationV1[] locations) => new(offset, category, locations);

    private static string LocationProjection(HybridCpuAbiLocationV2 location) => string.Join(':',
        location.ValueIdentity, location.Kind, string.Join(',', location.Registers),
        location.StackOffsetBytes, location.SizeBytes);
}
