using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HybridCPU.Compiler.Core;
using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.Runtime;
using HybridCPU.Compiler.Core.IR.Artifacts;
using HybridCPU.Compiler.Core.IR.Authority;
using HybridCPU.Compiler.Core.IR.Contours;
using HybridCPU.Compiler.Core.IR.Intent;
using HybridCPU.Compiler.Core.Threading;
using HybridCPU_ISE.Arch;
using HybridCPU_ISE.Tests.TestHelpers;
using Xunit;
using YAKSys_Hybrid_CPU;
using YAKSys_Hybrid_CPU.Arch;
using YAKSys_Hybrid_CPU.Core;
using YAKSys_Hybrid_CPU.Core.Execution.ExternalAccelerators.Descriptors;
using static YAKSys_Hybrid_CPU.Processor.CPU_Core;

namespace HybridCPU_ISE.Tests.CompilerTests;

/// <summary>
/// Phase 02 positive/negative artifact baselines. These are compatibility
/// artifacts and must not be read as production-lowering approval.
/// </summary>
public sealed class CompilerPhase02GoldenArtifactHarnessTests
{
    private const string PositiveManifestPath =
        "Tests/TestData/CompilerGoldenArtifacts/positive-manifest.json";
    private const string NegativeManifestPath =
        "Tests/TestData/CompilerGoldenArtifacts/negative-manifest.json";

    private static readonly CompilerGoldenArtifactSpec[] PositiveSpecs =
    [
        new(
            "scalar-vliw-add-carrier",
            "CompilerPhase02GoldenArtifactHarnessTests.ScalarVliwCandidate",
            SemanticIntentKind.ScalarAlu,
            ExecutionContourKind.NativeVliwScalar,
            "ADD",
            "HybridCpuThreadCompilerContext.CompileInstruction",
            "phase03-explicit-production-gate-required",
            "phase02-no-cross-contour-fallback:scalar-vliw-add-carrier"),
        new(
            "vector-vload-helper-carrier",
            "CompilerPhase02GoldenArtifactHarnessTests.VectorTransferHelper",
            SemanticIntentKind.VectorStream,
            ExecutionContourKind.StreamEngineVector,
            "VLOAD",
            "HybridCpuThreadCompilerContext.CompileVloadWithDecision",
            "phase03-explicit-production-gate-required",
            "phase02-no-cross-contour-fallback:vector-vload-helper-carrier"),
        new(
            "matrixtile-helper-carrier",
            "CompilerPhase02GoldenArtifactHarnessTests.MatrixTileHelper",
            SemanticIntentKind.MatrixTile,
            ExecutionContourKind.MatrixTileHelperOnly,
            "MTILE_LOAD",
            "HybridCpuThreadCompilerContext.CompileMtileLoadWithDecision",
            "phase03-explicit-production-gate-required",
            "phase02-no-cross-contour-fallback:matrixtile-helper-carrier"),
        new(
            "dsc-lane6-compatibility-carrier",
            "CompilerPhase02GoldenArtifactHarnessTests.DscLane6Compatibility",
            SemanticIntentKind.DmaStreamCompute,
            ExecutionContourKind.DmaStreamComputeLane6,
            "DmaStreamCompute",
            "HybridCpuThreadCompilerContext.CompileDmaStreamCompute",
            "phase03-explicit-production-gate-required",
            "phase02-no-cross-contour-fallback:dsc-lane6-compatibility-carrier"),
        new(
            "l7-sdc-lane7-compatibility-carrier",
            "CompilerPhase02GoldenArtifactHarnessTests.L7SdcLane7Compatibility",
            SemanticIntentKind.ExternalAcceleratorCommand,
            ExecutionContourKind.L7SdcLane7,
            "ACCEL_SUBMIT",
            "HybridCpuThreadCompilerContext.CompileAcceleratorSubmit",
            "phase03-explicit-production-gate-required",
            "phase02-no-cross-contour-fallback:l7-sdc-lane7-compatibility-carrier")
    ];

    private static readonly string[] RequiredNegativeArtifactIds =
    [
        "unknown-contour",
        "cross-contour-provider-analysis",
        "descriptorless-l7-submit",
        "dsc-missing-owner-domain-guard",
        "vector-zero-length",
        "vector-zero-stride",
        "vmx-emission-attempt",
        "securecompute-emission-attempt"
    ];

    [Fact]
    public void PositiveCompatibilityPackages_MatchSeparatedGoldenBaselines()
    {
        string repoRoot = CompatFreezeScanner.FindCompilerRepoRoot();
        CompilerGoldenArtifactManifest expected =
            CompilerGoldenArtifactHarness.LoadManifest(repoRoot, PositiveManifestPath);
        CompilerGoldenArtifactHarness.AssertManifestShape(
            expected,
            PositiveSpecs.Select(static spec => spec.ArtifactId).ToArray());

        CompilerGoldenArtifactManifest actual = new()
        {
            SchemaVersion = CompilerGoldenArtifactHarness.SchemaVersion,
            Entries = PositiveSpecs
                .Select(spec => CompilerGoldenArtifactHarness.Snapshot(Project(spec), spec))
                .ToList()
        };

        Assert.Equal(
            CompilerGoldenArtifactHarness.Render(expected),
            CompilerGoldenArtifactHarness.Render(actual));
    }

    [Fact]
    public void PositivePackages_KeepEnvelopeSeparationAndRuntimeAuthorityPending()
    {
        foreach (CompilerGoldenArtifactSpec spec in PositiveSpecs)
        {
            CompilerEmissionPackage package = Project(spec);

            Assert.NotNull(package.Carrier);
            Assert.NotNull(package.Sideband);
            Assert.NotNull(package.TypedSlotFacts);
            Assert.NotNull(package.RuntimeBridgeInput);
            Assert.True(package.SeparationProof.CarrierSeparatedFromSideband);
            Assert.True(package.SeparationProof.DescriptorSeparatedFromAuthority);
            Assert.True(package.SeparationProof.TypedSlotFactsSeparatedFromLegality);
            Assert.True(package.SeparationProof.EvidenceSeparatedFromProductionLowering);
            Assert.True(package.SeparationProof.BridgeSeparatedFromExecution);
            Assert.Equal(CompilerExecutionClaim.RuntimeExecutionRequired, package.Carrier!.Header.ExecutionClaim);
            Assert.Equal(CompilerExecutionClaim.NoExecutionClaim, package.Evidence.Header.ExecutionClaim);
            Assert.True(package.RuntimeBridgeInput!.RuntimeLegalityAStillRequired);
            Assert.True(package.RuntimeBridgeInput.RuntimeLegalityBStillRequired);
            Assert.True(package.RuntimeBridgeInput.RuntimeCommitStillRequired);
            Assert.True(package.RuntimeBridgeInput.RuntimeRetireStillRequired);
            Assert.True(package.RuntimeBridgeInput.RuntimePublicationStillRequired);
            Assert.DoesNotContain(
                package.SeparationProof.Notes,
                static note => note.Contains("production lowering authority", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void NegativeManifest_ContainsRequiredFailClosedCasesWithoutCarrierHashes()
    {
        string repoRoot = CompatFreezeScanner.FindCompilerRepoRoot();
        CompilerGoldenArtifactManifest manifest =
            CompilerGoldenArtifactHarness.LoadManifest(repoRoot, NegativeManifestPath);
        CompilerGoldenArtifactHarness.AssertManifestShape(manifest, RequiredNegativeArtifactIds);

        foreach (CompilerGoldenArtifactEntry entry in manifest.Entries)
        {
            Assert.Equal("not-present", entry.CarrierWordsOrBytesHash);
            Assert.Equal("rejected/no-emission", entry.ExpectedOutcome);
            Assert.Equal("NotApplicable", entry.IseDecodeParityStatus);
            Assert.Contains("not execution", entry.ExplicitNonClaims, StringComparison.Ordinal);
            Assert.Contains("no-fallback", entry.NoFallbackProofId, StringComparison.Ordinal);
        }
    }

    private static CompilerEmissionPackage Project(CompilerGoldenArtifactSpec spec)
    {
        HybridCpuThreadCompilerContext context = spec.ArtifactId switch
        {
            "scalar-vliw-add-carrier" => CreateScalarContext(),
            "vector-vload-helper-carrier" => CreateVectorContext(),
            "matrixtile-helper-carrier" => CreateMatrixTileContext(),
            "dsc-lane6-compatibility-carrier" => CreateDscContext(),
            "l7-sdc-lane7-compatibility-carrier" => CreateL7Context(),
            _ => throw new ArgumentOutOfRangeException(nameof(spec), spec.ArtifactId, "Unknown Phase 02 golden fixture.")
        };

        HybridCpuCompiledProgram compiledProgram = context.CompileProgram();
        return HybridCpuCompiledProgramEnvelopeAdapter.Instance.Project(
            compiledProgram,
            new CompilerArtifactProjectionOptions(
                spec.IntentKind,
                spec.Contour,
                spec.ProducerSurface,
                "Phase 02 compatibility/helper golden artifact; runtime authority remains pending."));
    }

    private static HybridCpuThreadCompilerContext CreateScalarContext()
    {
        HybridCpuThreadCompilerContext context = new HybridCpuThreadCompilerContext(virtualThreadId: 0);
        context.CompileInstruction(
            (uint)InstructionsEnum.ADD,
            (byte)DataTypeEnum.INT32,
            predicate: 0,
            immediate: 0,
            destSrc1: VLIW_Instruction.PackArchRegs(1, 2, 0),
            src2: 0,
            streamLength: 0,
            stride: 0,
            StealabilityPolicy.NotStealable);
        return context;
    }

    private static HybridCpuThreadCompilerContext CreateVectorContext()
    {
        HybridCpuThreadCompilerContext context = new HybridCpuThreadCompilerContext(virtualThreadId: 0);
        context.CompileVloadWithDecision(
            CompilerVectorTransferMemoryAddressAbi.Create(0x200),
            CompilerVectorTransferMemoryAddressAbi.Create(0x300),
            CompilerVectorTransferShapeAbi.CreateContiguous(HybridCpuDataType.INT32, 4));
        return context;
    }

    private static HybridCpuThreadCompilerContext CreateMatrixTileContext()
    {
        HybridCpuThreadCompilerContext context = new HybridCpuThreadCompilerContext(virtualThreadId: 0);
        context.CompileMtileLoadWithDecision(
            CompilerMatrixTileTileOperand.Create(1),
            CompilerMatrixTileDescriptorAbi.Create(2, 2, HybridCpuDataType.INT8),
            CompilerMatrixTileMemoryFaultAbiInputs.Create(0x100));
        return context;
    }

    private static HybridCpuThreadCompilerContext CreateDscContext()
    {
        HybridCpuThreadCompilerContext context = new HybridCpuThreadCompilerContext(virtualThreadId: 0);
        context.CompileDmaStreamCompute(DmaStreamComputeTestDescriptorFactory.CreateDescriptor());
        return context;
    }

    private static HybridCpuThreadCompilerContext CreateL7Context()
    {
        AcceleratorCommandDescriptor descriptor = L7SdcTestDescriptorFactory.ParseValidDescriptor();
        HybridCpuThreadCompilerContext context =
            L7SdcCompilerEmissionTests.CreateContextForDescriptor(descriptor);
        context.CompileAcceleratorSubmit(
            IrAcceleratorIntent.ForMatMul(descriptor, tokenDestinationRegister: 9),
            CompilerAcceleratorCapabilityModel.ReferenceMatMul);
        return context;
    }
}
