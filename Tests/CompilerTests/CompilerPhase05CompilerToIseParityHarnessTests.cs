using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HybridCPU.Compiler.Core.API.Facade;
using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.Runtime;
using TypedSlotBundleFacts = HybridCPU.Compiler.Core.IR.IrTypedSlotBundleFacts;
using HybridCPU.Compiler.Core.IR.Artifacts;
using HybridCPU.Compiler.Core.IR.Contours;
using HybridCPU.Compiler.Core.IR.Intent;
using HybridCPU.Compiler.Core.Threading;
using HybridCPU_ISE.Arch;
using HybridCPU_ISE.Tests.TestHelpers;
using Xunit;
using Xunit.Sdk;
using YAKSys_Hybrid_CPU;
using YAKSys_Hybrid_CPU.Core;
using YAKSys_Hybrid_CPU.Core.Execution.DmaStreamCompute;
using YAKSys_Hybrid_CPU.Core.Execution.ExternalAccelerators.Descriptors;
using static YAKSys_Hybrid_CPU.Processor.CPU_Core;

namespace HybridCPU_ISE.Tests.CompilerTests;

/// <summary>
/// Phase 05 compiler-to-ISE parity tests. These exercise carrier decode,
/// bundle byte round-trip, lane/slot facts, and runtime-bridge dependencies.
/// They do not execute projected MicroOps and do not promote compatibility
/// or helper emission into production lowering.
/// </summary>
public sealed class CompilerPhase05CompilerToIseParityHarnessTests
{
    [Fact]
    public void CompatibilityGoldenPackages_MatchCanonicalIseDecodeAndByteRoundTrip()
    {
        string repoRoot = CompatFreezeScanner.FindRepoRoot();
        CompilerGoldenArtifactManifest manifest =
            CompilerGoldenArtifactHarness.LoadManifest(
                repoRoot,
                "HybridCPU_ISE.Tests/TestData/CompilerGoldenArtifacts/positive-manifest.json");

        foreach (ParityCase parityCase in CompatibilityCases())
        {
            CompilerEmissionPackage package = parityCase.CreatePackage();
            CompilerToIseParitySnapshot snapshot =
                CompilerToIseParityHarness.AssertContourAndOpcode(
                    package,
                    parityCase.Contour,
                    parityCase.Opcode);

            CompilerGoldenArtifactEntry expected = Assert.Single(
                manifest.Entries,
                entry => entry.ArtifactId == parityCase.ArtifactId);
            Assert.Equal(expected.CarrierWordsOrBytesHash, snapshot.CarrierBytesHash);
            Assert.Equal(snapshot.CarrierBytesHash, snapshot.ReencodedBytesHash);
            Assert.Equal(parityCase.SlotClass, Assert.Single(snapshot.Slots).SlotClass);
            Assert.Equal(
                SlotClassLaneMap.GetLaneMask(parityCase.SlotClass),
                Assert.Single(snapshot.Slots).EligibleLaneMask);
            CompilerToIseParityHarness.AssertRuntimeAuthorityPending(package);
        }
    }

    [Fact]
    public void ScalarLoadStoreAndBranchContours_MatchCanonicalIseLaneOwnership()
    {
        foreach (ParityCase parityCase in LoadStoreContourCases())
        {
            CompilerEmissionPackage package = parityCase.CreatePackage();
            CompilerToIseParitySnapshot snapshot =
                CompilerToIseParityHarness.AssertContourAndOpcode(
                    package,
                    parityCase.Contour,
                    parityCase.Opcode);

            Assert.NotEmpty(snapshot.Slots);
            Assert.All(
                snapshot.Slots,
                slot =>
                {
                    Assert.Equal(parityCase.SlotClass, slot.SlotClass);
                    Assert.Equal(
                        SlotClassLaneMap.GetLaneMask(parityCase.SlotClass),
                        slot.EligibleLaneMask);
                });
            CompilerToIseParityHarness.AssertRuntimeAuthorityPending(package);
        }
    }

    [Fact]
    public void BranchCompatibilityCarrier_PinMismatchFailsClosed()
    {
        Assert.Throws<EqualException>(
            () => CompilerToIseParityHarness.Capture(CreateBranchPackage()));
    }

    [Fact]
    public void MatrixTileHelperCarrier_MatchesCanonicalIseLane6Parity()
    {
        CompilerEmissionPackage package = CreateMatrixTilePackage();
        CompilerToIseParitySnapshot snapshot = CompilerToIseParityHarness.AssertContourAndOpcode(
            package, ExecutionContourKind.MatrixTileHelperOnly, InstructionsEnum.MTILE_LOAD);
        CompilerToIseParitySlot slot = Assert.Single(snapshot.Slots);
        Assert.Equal(6, slot.SlotIndex);
        Assert.Equal(SlotClass.MatrixTileStreamClass, slot.SlotClass);
        Assert.Equal(SlotPinningKind.HardPinned, slot.PinningKind);
        Assert.Equal(0x40, slot.EligibleLaneMask);
        Assert.Equal(snapshot.CarrierBytesHash, snapshot.ReencodedBytesHash);
        CompilerToIseParityHarness.AssertRuntimeAuthorityPending(package);
    }

    [Fact]
    public void DscDescriptorBackedCarrier_MatchesCanonicalIseLane6Parity()
    {
        CompilerToIseParitySnapshot snapshot =
            CompilerToIseParityHarness.AssertContourAndOpcode(
                CreateDscPackage(),
                ExecutionContourKind.DmaStreamComputeLane6,
                InstructionsEnum.DmaStreamCompute);

        CompilerToIseParitySlot slot = Assert.Single(snapshot.Slots);
        Assert.Equal(SlotClass.DmaStreamClass, slot.SlotClass);
        Assert.Equal(SlotPinningKind.HardPinned, slot.PinningKind);
        Assert.Equal(SlotClassLaneMap.GetLaneMask(SlotClass.DmaStreamClass), slot.EligibleLaneMask);
        CompilerToIseParityHarness.AssertRuntimeAuthorityPending(CreateDscPackage());
    }

    [Fact]
    public void TamperedCarrierBytes_FailClosedBeforeParityCanPass()
    {
        CompilerEmissionPackage package = CompatibilityCases().First().CreatePackage();
        byte[] tamperedImage = package.Carrier!.Image.SerializedImage.ToArray();
        tamperedImage[^1] ^= 0x01;
        CompilerEmissionPackage tampered = package with
        {
            Carrier = package.Carrier with
            {
                Image = package.Carrier.Image with
                {
                    SerializedImage = tamperedImage
                }
            }
        };

        Assert.Throws<EqualException>(
            () => CompilerToIseParityHarness.Capture(tampered));
    }

    [Fact]
    public void TamperedTypedSlotFacts_FailClosedBeforeAuthorityInterpretation()
    {
        CompilerEmissionPackage package = CompatibilityCases()
            .Single(candidate => candidate.ArtifactId == "vector-vload-helper-carrier")
            .CreatePackage();
        TypedSlotFactsEnvelope facts = package.TypedSlotFacts!;
        TypedSlotBundleFacts[] tamperedFacts = facts.Facts.ToArray();
        tamperedFacts[0] = tamperedFacts[0] with
        {
            LsuCount = checked((byte)(tamperedFacts[0].LsuCount + 1))
        };
        CompilerEmissionPackage tampered = package with
        {
            TypedSlotFacts = facts with { Facts = tamperedFacts }
        };

        Assert.Throws<EqualException>(
            () => CompilerToIseParityHarness.Capture(tampered));
    }

    [Fact]
    public void DscAndL7Packages_CannotBeRelabeledAcrossContours()
    {
        CompilerEmissionPackage l7Package = CompatibilityCases()
            .Single(candidate => candidate.ArtifactId == "l7-sdc-lane7-compatibility-carrier")
            .CreatePackage();
        CompilerEmissionPackage relabeled = l7Package with
        {
            Identity = l7Package.Identity with
            {
                ContourKind = ExecutionContourKind.DmaStreamComputeLane6
            }
        };

        Assert.Throws<ContainsException>(
            () => CompilerToIseParityHarness.AssertContourAndOpcode(
                relabeled,
                ExecutionContourKind.DmaStreamComputeLane6,
                InstructionsEnum.DmaStreamCompute));
    }

    private static IEnumerable<ParityCase> CompatibilityCases()
    {
        yield return new(
            "scalar-vliw-add-carrier",
            SemanticIntentKind.ScalarAlu,
            ExecutionContourKind.NativeVliwScalar,
            InstructionsEnum.ADD,
            SlotClass.AluClass,
            CreateScalarPackage);
        yield return new(
            "vector-vload-helper-carrier",
            SemanticIntentKind.VectorStream,
            ExecutionContourKind.StreamEngineVector,
            InstructionsEnum.VLOAD,
            SlotClass.LsuClass,
            CreateVectorPackage);
        yield return new(
            "l7-sdc-lane7-compatibility-carrier",
            SemanticIntentKind.ExternalAcceleratorCommand,
            ExecutionContourKind.L7SdcLane7,
            InstructionsEnum.ACCEL_SUBMIT,
            SlotClass.SystemSingleton,
            CreateL7Package);
    }

    private static IEnumerable<ParityCase> LoadStoreContourCases()
    {
        yield return new(
            "native-load-store-lw",
            SemanticIntentKind.LoadStore,
            ExecutionContourKind.NativeVliwLoadStore,
            InstructionsEnum.LW,
            SlotClass.LsuClass,
            CreateLoadStorePackage);
    }

    internal static CompilerEmissionPackage CreateScalarPackage() =>
        Project(
            CreateContextWithInstruction(
                InstructionsEnum.ADD,
                destSrc1: VLIW_Instruction.PackArchRegs(1, 2, 0),
                predicate: 0),
            SemanticIntentKind.ScalarAlu,
            ExecutionContourKind.NativeVliwScalar,
            "Phase 05 scalar parity fixture");

    internal static CompilerEmissionPackage CreateLoadStorePackage() =>
        Project(
            CreateContextWithInstruction(
                InstructionsEnum.LW,
                destSrc1: VLIW_Instruction.PackArchRegs(9, 1, 0),
                src2: 0x40),
            SemanticIntentKind.LoadStore,
            ExecutionContourKind.NativeVliwLoadStore,
            "Phase 05 load/store parity fixture");

    internal static CompilerEmissionPackage CreateVectorPackage()
    {
        HybridCpuThreadCompilerContext context = new HybridCpuThreadCompilerContext(virtualThreadId: 0);
        context.CompileVloadWithDecision(
            CompilerVectorTransferMemoryAddressAbi.Create(0x200),
            CompilerVectorTransferMemoryAddressAbi.Create(0x300),
            CompilerVectorTransferShapeAbi.CreateContiguous(HybridCpuDataType.INT32, 4));
        return Project(
            context,
            SemanticIntentKind.VectorStream,
            ExecutionContourKind.StreamEngineVector,
            "Phase 05 vector parity fixture");
    }

    internal static CompilerEmissionPackage CreateMatrixTilePackage()
    {
        HybridCpuThreadCompilerContext context = new HybridCpuThreadCompilerContext(virtualThreadId: 0);
        context.CompileMtileLoadWithDecision(
            CompilerMatrixTileTileOperand.Create(1),
            CompilerMatrixTileDescriptorAbi.Create(2, 2, HybridCpuDataType.INT8),
            CompilerMatrixTileMemoryFaultAbiInputs.Create(0x100));
        return Project(
            context,
            SemanticIntentKind.MatrixTile,
            ExecutionContourKind.MatrixTileHelperOnly,
            "Phase 05 MatrixTile parity fixture");
    }

    internal static CompilerEmissionPackage CreateDscPackage()
    {
        HybridCpuThreadCompilerContext context = new HybridCpuThreadCompilerContext(virtualThreadId: 0);
        context.CompileDmaStreamCompute(DmaStreamComputeTestDescriptorFactory.CreateDescriptor());
        return Project(
            context,
            SemanticIntentKind.DmaStreamCompute,
            ExecutionContourKind.DmaStreamComputeLane6,
            "Phase 05 DSC parity fixture");
    }

    internal static CompilerEmissionPackage CreateL7Package()
    {
        AcceleratorCommandDescriptor descriptor = L7SdcTestDescriptorFactory.ParseValidDescriptor();
        HybridCpuThreadCompilerContext context =
            L7SdcCompilerEmissionTests.CreateContextForDescriptor(descriptor);
        context.CompileAcceleratorSubmit(
            IrAcceleratorIntent.ForMatMul(descriptor, tokenDestinationRegister: 9),
            CompilerAcceleratorCapabilityModel.ReferenceMatMul);
        return Project(
            context,
            SemanticIntentKind.ExternalAcceleratorCommand,
            ExecutionContourKind.L7SdcLane7,
            "Phase 05 L7 parity fixture");
    }

    internal static CompilerEmissionPackage CreateBranchPackage()
    {
        HybridCpuThreadCompilerContext context = new HybridCpuThreadCompilerContext(virtualThreadId: 0);
#pragma warning disable CS0618
        AppAsmFacade facade = new AppAsmFacade(0, context);
        AsmControlTarget target = facade.DefineEntryPoint("phase05.target");
        facade.Jump(target);
        facade.Nop();
        facade.MarkEntryPoint(target);
        facade.Nop();
#pragma warning restore CS0618
        return Project(
            context,
            SemanticIntentKind.BranchControl,
            ExecutionContourKind.NativeVliwBranchControl,
            "Phase 05 branch parity fixture");
    }

    private static HybridCpuThreadCompilerContext CreateContextWithInstruction(
        InstructionsEnum opcode,
        ulong destSrc1,
        ulong src2 = 0,
        byte predicate = 0xFF)
    {
        HybridCpuThreadCompilerContext context = new HybridCpuThreadCompilerContext(virtualThreadId: 0);
        context.CompileInstruction(
            (uint)opcode,
            (byte)DataTypeEnum.INT32,
            predicate,
            immediate: 0,
            destSrc1,
            src2,
            streamLength: 0,
            stride: 0,
            StealabilityPolicy.NotStealable);
        return context;
    }

    private static CompilerEmissionPackage Project(
        HybridCpuThreadCompilerContext context,
        SemanticIntentKind intentKind,
        ExecutionContourKind contourKind,
        string reason)
    {
        HybridCpuCompiledProgram compiledProgram = context.CompileProgram();
        return HybridCpuCompiledProgramEnvelopeAdapter.Instance.Project(
            compiledProgram,
            new CompilerArtifactProjectionOptions(
                intentKind,
                contourKind,
                "CompilerPhase05CompilerToIseParityHarnessTests",
                reason));
    }

    private sealed record ParityCase(
        string ArtifactId,
        SemanticIntentKind IntentKind,
        ExecutionContourKind Contour,
        InstructionsEnum Opcode,
        SlotClass SlotClass,
        Func<CompilerEmissionPackage> CreatePackage);
}
