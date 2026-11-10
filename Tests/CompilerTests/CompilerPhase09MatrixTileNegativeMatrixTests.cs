using System;
using System.Linq;
using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.Runtime;
using HybridCPU.Compiler.Core.IR.Contours;
using HybridCPU.Compiler.Core.IR.Intent;
using HybridCPU.Compiler.Core.IR.Lowering;
using HybridCPU.Compiler.Core.Threading;
using HybridCPU_ISE.Arch;
using Xunit;
using YAKSys_Hybrid_CPU.Arch;
using YAKSys_Hybrid_CPU.CloseToHSL.Core.ISA.Instructions.NonVmx.Lanes00_03Vector.MatrixTile;
using static YAKSys_Hybrid_CPU.Processor.CPU_Core;
using DataTypeEnum = HybridCPU.Compiler.Core.IR.HybridCpuDataType;
using InstructionsEnum = HybridCPU.Compiler.Core.IR.HybridCpuOpcode;
using MatrixTileCanonicalDescriptorAbi = HybridCPU.Compiler.Core.IR.Matrix.MatrixTileCanonicalDescriptorAbi;
using MatrixTileNumericPolicy = HybridCPU.Compiler.Core.IR.Matrix.MatrixTileNumericPolicy;
using MatrixTileTransposeAliasPolicyKind = HybridCPU.Compiler.Core.IR.Matrix.MatrixTileTransposeAliasPolicyKind;

namespace HybridCPU_ISE.Tests.CompilerTests;

public sealed class CompilerPhase09MatrixTileNegativeMatrixTests
{
    [Fact]
    public void MatrixTileContract_OnlyCurrentFourHelperRowsAndNoFallbackFlags()
    {
        InstructionsEnum[] expectedOpcodes =
        [
            InstructionsEnum.MTILE_LOAD,
            InstructionsEnum.MTILE_STORE,
            InstructionsEnum.MTILE_MACC,
            InstructionsEnum.MTRANSPOSE
        ];

        CompilerMatrixTilePositiveEmissionRow[] rows =
            CompilerMatrixTilePositiveEmissionAbiContract.Rows.ToArray();

        Assert.Equal(expectedOpcodes, rows.Select(static row => row.Opcode).ToArray());
        Assert.All(rows, row => Assert.False(row.UsesFallbackPath));
        Assert.All(rows, row => Assert.False(row.UsesAliasPromotion));
        Assert.All(rows, row => Assert.True(row.RuntimeOwnedLegalityIsFinal));
        Assert.False(CompilerMatrixTilePositiveEmissionAbiContract.AllowsCompilerToOverrideRuntimeLegality);
        Assert.False(CompilerMatrixTilePositiveEmissionAbiContract.UsesFallbackPath);
        Assert.False(CompilerMatrixTilePositiveEmissionAbiContract.UsesAliasPromotion);
        Assert.Equal(
            "DirectMatrixTileEmissionNoScalarVectorDotLane6Lane7VmxOrBackendFallback",
            CompilerMatrixTilePositiveEmissionAbiContract.NoFallbackDecision);

        InstructionsEnum[] forbiddenAliasOpcodes =
        [
            InstructionsEnum.VLOAD,
            InstructionsEnum.VSTORE,
            InstructionsEnum.VDOT,
            InstructionsEnum.VDOTU,
            InstructionsEnum.VDOTF,
            InstructionsEnum.VDOT_FP8,
            InstructionsEnum.VDOT_WIDE,
            InstructionsEnum.DmaStreamCompute,
            InstructionsEnum.ACCEL_SUBMIT
        ];

        foreach (InstructionsEnum opcode in forbiddenAliasOpcodes)
        {
            Assert.False(CompilerMatrixTilePositiveEmissionAbiContract.IsMatrixTilePositiveOpcode(opcode));
        }
    }

    [Fact]
    public void MatrixTileUnsupportedShapeAndDtypeRejectBeforeEmission()
    {
        HybridCpuThreadCompilerContext context = new HybridCpuThreadCompilerContext(virtualThreadId: 0);

        CompilerMatrixTileDescriptorAbi zeroRowDescriptor = new CompilerMatrixTileDescriptorAbi(
            MatrixTileCanonicalDescriptorAbi.Create(0, 2, 1, 2),
            DataTypeEnum.INT8);
        Assert.Throws<ArgumentException>(() =>
            context.CompileMtileLoad(
                CompilerMatrixTileTileOperand.Create(1),
                zeroRowDescriptor,
                CompilerMatrixTileMemoryFaultAbiInputs.Create(0x100)));
        Assert.Equal(0, context.InstructionCount);

        CompilerMatrixTileDescriptorAbi unsupportedDtypeDescriptor = new CompilerMatrixTileDescriptorAbi(
            MatrixTileCanonicalDescriptorAbi.Create(2, 2, 1, 2),
            DataTypeEnum.FLOAT8_E4M3);
        CompilerMatrixTileAccumulatorPolicyAbi int8AccumulatorPolicy =
            CompilerMatrixTileAccumulatorPolicyAbi.CreateForRuntimeDerivedFootprint(
                MatrixTileCanonicalDescriptorAbi.Create(2, 2, 1, 2));

        ArgumentException dtypeException = Assert.Throws<ArgumentException>(() =>
            context.CompileMtileMacc(
                CompilerMatrixTileTileOperand.Create(1),
                CompilerMatrixTileTileOperand.Create(2),
                CompilerMatrixTileTileOperand.Create(3),
                unsupportedDtypeDescriptor,
                int8AccumulatorPolicy));
        Assert.Contains("numeric policy sideband", dtypeException.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, context.InstructionCount);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CompilerMatrixTilePositiveEmissionAbiContract.CreateRuntimeNumericPolicy(DataTypeEnum.FLOAT8_E4M3));
    }

    [Fact]
    public void MatrixTileUnsupportedLayoutAndAccumulatorRejectBeforeEmission()
    {
        HybridCpuThreadCompilerContext context = new HybridCpuThreadCompilerContext(virtualThreadId: 0);
        CompilerMatrixTileDescriptorAbi descriptor = CompilerMatrixTileDescriptorAbi.Create(
            rows: 2,
            columns: 2,
            DataTypeEnum.INT8);

        CompilerMatrixTileTransposePolicyAbi wrongTransposeLayout = new CompilerMatrixTileTransposePolicyAbi(
            MatrixTileCanonicalDescriptorAbi.Create(2, 2, 1, 2),
            MatrixTileTransposeAliasPolicyKind.OutOfPlaceOrSquareInPlaceOnly)
        {
            MatrixTileLayoutPolicy = NativeTransportRuntimeAdapter.ToCore(MatrixTileLayoutPolicyAbi.CreateMaccPolicy())
        };

        ArgumentException layoutException = Assert.Throws<ArgumentException>(() =>
            context.CompileMtranspose(
                CompilerMatrixTileTileOperand.Create(1),
                CompilerMatrixTileTileOperand.Create(2),
                descriptor,
                wrongTransposeLayout));
        Assert.Contains("layout policy", layoutException.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, context.InstructionCount);

        MatrixTileNumericPolicy unsupportedNumeric =
            CompilerMatrixTilePositiveEmissionAbiContract.CreateRuntimeNumericPolicy(DataTypeEnum.INT8)
                with
            { AbiVersion = MatrixTileNumericPolicyAbi.CurrentAbiVersion + 1 };
        CompilerMatrixTileAccumulatorPolicyAbi unsupportedAccumulator =
            CompilerMatrixTileAccumulatorPolicyAbi.CreateForRuntimeDerivedFootprint(
                descriptor.CanonicalDescriptor,
                unsupportedNumeric);

        ArgumentException accumulatorException = Assert.Throws<ArgumentException>(() =>
            context.CompileMtileMacc(
                CompilerMatrixTileTileOperand.Create(1),
                CompilerMatrixTileTileOperand.Create(2),
                CompilerMatrixTileTileOperand.Create(3),
                descriptor,
                unsupportedAccumulator));
        Assert.Contains("numeric policy sideband", accumulatorException.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, context.InstructionCount);
    }

    [Fact]
    public void MatrixTileContourProviderRejectsLoweringWithoutScalarVectorOrStreamFallback()
    {
        CompilerSemanticIntent intent = new CompilerSemanticIntent(
            SemanticIntentKind.MatrixTile,
            "MTILE",
            RequiresDescriptor: false,
            RequiresSideband: true,
            RequiresToken: false,
            RequiresRuntimeLegality: true,
            IsCompatibilityProjection: false,
            IsPolicyAdmissionOnly: false,
            IsHelperAbiOnly: true,
            IsParserOnly: false,
            "Phase09 MatrixTile helper-only negative gate.");
        CompilerLoweringContext context = new CompilerLoweringContext(
            new CompilerTargetProfile("phase09-matrix-tile"),
            "CompilerPhase09MatrixTileNegativeMatrixTests");

        IContourAnalyzer analyzer =
            DefaultContourLoweringProviderRegistry.Instance.ResolveAnalyzer(ExecutionContourKind.MatrixTileHelperOnly);
        ContourAnalysisReport analysis = analyzer.Analyze(intent, context);
        IContourLoweringProvider provider =
            DefaultContourLoweringProviderRegistry.Instance.ResolveProvider(ExecutionContourKind.MatrixTileHelperOnly);
        CompilerLoweringDecision decision = provider.Lower(intent, analysis, context);

        Assert.True(analysis.ProviderAvailable);
        Assert.Equal(ExecutionContourKind.MatrixTileHelperOnly, analysis.ContourKind);
        Assert.Equal(CompilerCapabilityObservationState.HelperOnly, analysis.CapabilityObservation.State);
        Assert.Contains("scalar/vector/Stream fallback is forbidden", analysis.CapabilityObservation.Reason, StringComparison.Ordinal);
        Assert.Equal(CompilerLoweringDecisionKind.Rejected, decision.DecisionKind);
        Assert.Equal(CompilerRejectReason.RuntimeAuthorityOwned, Assert.Single(decision.RejectReasons));
        Assert.Equal(CompilerEmissionClass.NoEmission, decision.EmissionClass);
        Assert.Equal(CompilerProductionLoweringStatus.Rejected, decision.ProductionLoweringStatus);
        Assert.False(decision.FallbackPolicy.AllowsCrossContourFallback);
        Assert.Equal(FallbackPolicyKind.Forbidden, decision.NoFallbackProof.PolicyKind);
    }
}
