using System.Reflection;
using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.IR.Resources;
using HybridCPU.Compiler.Core.Target;
using HybridCPU.Compiler.Native;
using YAKSys_Hybrid_CPU.Core.Registers;

namespace HybridCPU_ISE.Tests.CompilerTests;

public sealed class CompilerRefPlan5Phase08ATargetMachineTests
{
    [Fact]
    public void TargetIdentityAndLayout_ArePinnedFrontendNeutralFacts()
    {
        HybridCpuTargetMachineContractV1 target = HybridCpuTargetMachineContractV1.Default;

        Assert.Equal(HybridCpuTargetEndianness.Little, target.Endianness);
        Assert.Equal(64, HybridCpuTargetMachineContractV1.PointerBitWidth);
        Assert.Equal(8, HybridCpuTargetMachineContractV1.PointerAbiAlignmentBytes);
        Assert.Equal("hybridcpuv2-unknown-none", HybridCpuTargetMachineContractV1.TargetTriple);
        Assert.Equal("hybridcpu-native-datalayout/v1", HybridCpuTargetMachineContractV1.DataLayoutVersion);
        Assert.DoesNotContain("llvm", HybridCpuTargetMachineContractV1.DataLayoutIdentity, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("windows", HybridCpuTargetMachineContractV1.DataLayoutIdentity, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(8, 1, 1)]
    [InlineData(16, 2, 2)]
    [InlineData(32, 4, 4)]
    [InlineData(64, 8, 8)]
    public void ScalarLayout_MatchesPinnedNativeWidths(int bitWidth, int size, int alignment)
    {
        Assert.True(HybridCpuTargetMachineContractV1.Default.TryGetScalarLayout(bitWidth, out HybridCpuScalarLayoutV1 layout));
        Assert.Equal(size, layout.SizeBytes);
        Assert.Equal(alignment, layout.AbiAlignmentBytes);
    }

    [Fact]
    public void UnknownScalarWidth_AndUnknownAddressSpaceFailClosed()
    {
        HybridCpuTargetMachineContractV1 target = HybridCpuTargetMachineContractV1.Default;

        Assert.False(target.TryGetScalarLayout(128, out _));
        Assert.Equal(HybridCpuTargetFactSupport.Unknown, target.GetAddressSpaceSupport(IrAddressSpaceIdentity.Unknown));
        Assert.Equal(HybridCpuTargetFactSupport.Unsupported, target.GetAddressSpaceSupport(IrAddressSpaceIdentity.ThreadLocal));
        Assert.Equal(HybridCpuTargetFactSupport.Supported, target.GetAddressSpaceSupport(IrAddressSpaceIdentity.Generic));
    }

    [Fact]
    public void AggregateLayout_IsNaturalDeterministicAndRejectsUnsupportedAlignment()
    {
        HybridCpuTargetMachineContractV1 target = HybridCpuTargetMachineContractV1.Default;
        HybridCpuAggregateFieldV1[] fields =
        [
            new("a", 1, 1),
            new("b", 8, 8),
            new("c", 2, 2)
        ];

        Assert.True(target.TryLayoutAggregate(fields, out HybridCpuAggregateLayoutV1 first));
        Assert.True(target.TryLayoutAggregate(fields.ToArray(), out HybridCpuAggregateLayoutV1 second));
        Assert.Equal(first.SizeBytes, second.SizeBytes);
        Assert.Equal(first.AlignmentBytes, second.AlignmentBytes);
        Assert.Equal(first.Fields.ToArray(), second.Fields.ToArray());
        Assert.Equal(24, first.SizeBytes);
        Assert.Equal(8, first.AlignmentBytes);
        Assert.Equal([0, 8, 16], first.Fields.Select(static field => field.OffsetBytes));
        Assert.False(target.TryLayoutAggregate([new("bad", 16, 16)], out _));
    }

    [Fact]
    public void ArchitecturalRegisterInventory_MatchesLiveHybridCpuV2Namespace()
    {
        HybridCpuTargetMachineContractV1 target = HybridCpuTargetMachineContractV1.Default;

        Assert.Equal(32, target.ArchitecturalRegisters.Count);
        Assert.Equal(ArchRegId.RegisterCount, target.ArchitecturalRegisters.Count);
        Assert.Equal(ArchRegId.MaxValue, target.ArchitecturalRegisters[^1].Id);
        Assert.Equal(Enumerable.Range(0, 32), target.ArchitecturalRegisters.Select(static register => register.Id));
        Assert.All(target.ArchitecturalRegisters, register =>
        {
            Assert.Equal(64, register.BitWidth);
            Assert.Equal(register.Id / HybridCpuMachineTopologyV1.RegistersPerGroup, register.RegisterGroup);
            Assert.Equal($"x{register.Id}", register.EncodedName);
        });
        Assert.True(target.ArchitecturalRegisters[0].IsFixedZero);
        Assert.False(target.ArchitecturalRegisters[0].IsAllocatable);
        Assert.All(target.ArchitecturalRegisters.Skip(1), static register => Assert.True(register.IsAllocatable));
        Assert.False(target.TryGetArchitecturalRegister(32, out _));
    }

    [Fact]
    public void RegisterClasses_DoNotInferVectorStorageFromExecutionContours()
    {
        string[] names = Enum.GetNames<HybridCpuArchitecturalRegisterClass>();

        Assert.Equal([nameof(HybridCpuArchitecturalRegisterClass.ScalarInteger64)], names);
        Assert.DoesNotContain(names, static name => name.Contains("Vector", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, static name => name.Contains("Matrix", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, static name => name.Contains("Stream", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SpecialState_IsExplicitAndNeverAllocatorOwned()
    {
        IReadOnlyList<HybridCpuSpecialStateContractV1> state = HybridCpuTargetMachineContractV1.Default.SpecialState;

        Assert.Equal(Enum.GetValues<HybridCpuSpecialStateClass>().Length, state.Count);
        Assert.All(state, static item => Assert.False(item.IsAllocatorOwned));
    }

    [Fact]
    public void PrimitiveCallBoundary_UsesOnlyExplicitCarrierOperands()
    {
        HybridCpuPrimitiveCallBoundaryV1 call = HybridCpuTargetMachineContractV1.Default.PrimitiveCallBoundary;

        Assert.True(call.UsesExplicitLinkDestination);
        Assert.True(call.UsesExplicitReturnBase);
        Assert.Empty(call.ImplicitUses);
        Assert.Empty(call.ImplicitDefs);
        Assert.Empty(call.ImplicitClobbers);
        Assert.Equal(HybridCpuTargetFactSupport.Unsupported, call.FullFunctionAbiSupport);
    }

    [Fact]
    public void VersionTripleAndLayoutSkew_AreRejectedDeterministically()
    {
        HybridCpuTargetMachineContractV1 target = HybridCpuTargetMachineContractV1.Default;

        Assert.Equal(HybridCpuTargetCompatibility.Compatible, target.CheckCompatibility(
            1,
            HybridCpuTargetMachineContractV1.TargetTriple,
            HybridCpuTargetMachineContractV1.DataLayoutVersion,
            HybridCpuTargetMachineContractV1.DataLayoutIdentity));
        Assert.Equal(HybridCpuTargetCompatibility.UnsupportedVersion, target.CheckCompatibility(
            2, HybridCpuTargetMachineContractV1.TargetTriple,
            HybridCpuTargetMachineContractV1.DataLayoutVersion,
            HybridCpuTargetMachineContractV1.DataLayoutIdentity));
        Assert.Equal(HybridCpuTargetCompatibility.TargetMismatch, target.CheckCompatibility(
            1, "host-unknown", HybridCpuTargetMachineContractV1.DataLayoutVersion,
            HybridCpuTargetMachineContractV1.DataLayoutIdentity));
        Assert.Equal(HybridCpuTargetCompatibility.DataLayoutMismatch, target.CheckCompatibility(
            1, HybridCpuTargetMachineContractV1.TargetTriple, "host-layout",
            HybridCpuTargetMachineContractV1.DataLayoutIdentity));
        Assert.Equal(HybridCpuTargetCompatibility.Unknown, target.CheckCompatibility(
            1, "", HybridCpuTargetMachineContractV1.DataLayoutVersion,
            HybridCpuTargetMachineContractV1.DataLayoutIdentity));
    }

    [Fact]
    public void TargetContract_DoesNotExposeRuntimeBackendAuthorityVocabulary()
    {
        string[] forbidden = ["Physical", "Prf", "Rename", "FreeList", "Scoreboard", "Occupancy", "Commit", "Retire"];
        PropertyInfo[] properties = typeof(HybridCpuTargetMachineContractV1).GetProperties(BindingFlags.Instance | BindingFlags.Public);

        Assert.All(properties, property => Assert.DoesNotContain(
            forbidden,
            token => property.Name.Contains(token, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void NativeFrontend_UsesPinnedTargetDigest_AndRejectsLayoutSkew()
    {
        NativeAssemblyFrontend frontend = new NativeAssemblyFrontend();
        NativeCompilationResult accepted = frontend.Compile(new NativeFrontendRequest(
            0,
            AssemblySource: "ADDI r1, r0, 1"));
        NativeCompilationResult rejected = frontend.Compile(new NativeFrontendRequest(
            0,
            AssemblySource: "ADDI r1, r0, 1")
        {
            BuildIdentity = NativeBuildIdentity.Unknown with { DataLayoutVersion = "host-default" }
        });
        NativeCompilationResult rejectedTriple = frontend.Compile(new NativeFrontendRequest(
            0,
            AssemblySource: "ADDI r1, r0, 1")
        {
            BuildIdentity = NativeBuildIdentity.Unknown with { TargetTriple = "host-unknown" }
        });

        Assert.True(accepted.Succeeded);
        Assert.Equal(HybridCpuTargetMachineContractV1.Default.ContractDigest, accepted.Artifacts!.Provenance.TargetDigest);
        Assert.Equal(NativeCompilationStatus.Unsupported, rejected.Status);
        Assert.Contains(rejected.Diagnostics, static diagnostic => diagnostic.Code == "HCN0008");
        Assert.Equal(NativeCompilationStatus.Unsupported, rejectedTriple.Status);
        Assert.Contains(rejectedTriple.Diagnostics, static diagnostic => diagnostic.Code == "HCN0008");
    }

    [Fact]
    public void TargetDigest_IsStableGoldenValue()
    {
        Assert.Equal(
            "ae6ee979176be7f21dc396fe0ddb5d7023d8b6de1722eb4d4652647dd3e99232",
            HybridCpuTargetMachineContractV1.Default.ContractDigest);
    }
}
