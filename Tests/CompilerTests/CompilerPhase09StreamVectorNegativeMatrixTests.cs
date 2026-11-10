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
using YAKSys_Hybrid_CPU;
using static YAKSys_Hybrid_CPU.Processor.CPU_Core;
using InstructionsEnum = HybridCPU.Compiler.Core.IR.HybridCpuOpcode;

namespace HybridCPU_ISE.Tests.CompilerTests;

public sealed class CompilerPhase09StreamVectorNegativeMatrixTests
{
    [Fact]
    public void VectorTransferContract_OnlyVloadVstoreAndNoScalarFallbackFlags()
    {
        InstructionsEnum[] expectedOpcodes =
        [
            InstructionsEnum.VLOAD,
            InstructionsEnum.VSTORE
        ];

        CompilerVectorTransferPositiveEmissionRow[] rows =
            CompilerVectorTransferPositiveEmissionAbiContract.Rows.ToArray();

        Assert.Equal(expectedOpcodes, rows.Select(static row => row.Opcode).ToArray());
        Assert.All(rows, row => Assert.False(row.UsesFallbackPath));
        Assert.All(rows, row => Assert.False(row.UsesAliasPromotion));
        Assert.All(rows, row => Assert.True(row.RuntimeOwnedLegalityIsFinal));
        Assert.False(CompilerVectorTransferPositiveEmissionAbiContract.AllowsCompilerToOverrideRuntimeLegality);
        Assert.False(CompilerVectorTransferPositiveEmissionAbiContract.UsesFallbackPath);
        Assert.False(CompilerVectorTransferPositiveEmissionAbiContract.UsesAliasPromotion);
        Assert.Equal(
            "DirectVectorTransferEmissionNoBaseMemoryBaseVectorScalarDotWideningFmaTransposeSegmentLane6Lane7VmxOrBackendFallback",
            CompilerVectorTransferPositiveEmissionAbiContract.NoFallbackDecision);

        InstructionsEnum[] forbiddenAliasOpcodes =
        [
            InstructionsEnum.VDOT,
            InstructionsEnum.VDOTU,
            InstructionsEnum.VDOTF,
            InstructionsEnum.VDOT_FP8,
            InstructionsEnum.VDOT_WIDE,
            InstructionsEnum.VTRANSPOSE,
            InstructionsEnum.MTILE_LOAD,
            InstructionsEnum.MTILE_STORE,
            InstructionsEnum.MTILE_MACC,
            InstructionsEnum.MTRANSPOSE,
            InstructionsEnum.DmaStreamCompute,
            InstructionsEnum.ACCEL_SUBMIT
        ];

        foreach (InstructionsEnum opcode in forbiddenAliasOpcodes)
        {
            Assert.False(CompilerVectorTransferPositiveEmissionAbiContract.IsVectorTransferPositiveOpcode(opcode));
        }
    }

    [Fact]
    public void VectorTransferUnsupportedShapeDtypeAndStrideRejectBeforeEmission()
    {
        HybridCpuThreadCompilerContext context = new HybridCpuThreadCompilerContext(virtualThreadId: 0);
        CompilerVectorTransferMemoryAddressAbi destination =
            CompilerVectorTransferMemoryAddressAbi.Create(0x200);
        CompilerVectorTransferMemoryAddressAbi source =
            CompilerVectorTransferMemoryAddressAbi.Create(0x300);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            context.CompileVload(
                destination,
                source,
                new CompilerVectorTransferShapeAbi(HybridCpuDataType.INT32, ElementCount: 0, StrideBytes: 4)));
        Assert.Equal(0, context.InstructionCount);

        Assert.Throws<ArgumentException>(() =>
            context.CompileVstore(
                source,
                destination,
                new CompilerVectorTransferShapeAbi(HybridCpuDataType.INT32, ElementCount: 4, StrideBytes: 2)));
        Assert.Equal(0, context.InstructionCount);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            context.CompileVload(
                destination,
                source,
                new CompilerVectorTransferShapeAbi((HybridCpuDataType)0xFF, ElementCount: 4, StrideBytes: 1)));
        Assert.Equal(0, context.InstructionCount);
    }

    [Theory]
    [InlineData(0, 4, false, false, "StreamLength == 0")]
    [InlineData(4, 0, false, false, "Stride == 0")]
    [InlineData(4, 4, true, false, "unsupported indexed")]
    [InlineData(4, 4, false, true, "unsupported 2D")]
    public void VectorTransferRecoveredMalformedCarrierFailsClosedWithoutLoadStoreFallback(
        uint streamLength,
        ushort stride,
        bool indexed,
        bool is2D,
        string expectedReason)
    {
        VLIW_Instruction instruction = new VLIW_Instruction
        {
            OpCode = (uint)InstructionsEnum.VLOAD,
            DataTypeValue = DataTypeEnum.INT32,
            DestSrc1Pointer = 0x200,
            Src2Pointer = 0x300,
            StreamLength = streamLength,
            Stride = stride,
            Indexed = indexed,
            Is2D = is2D
        };

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            new HybridCpuIrBuilder().BuildProgram(
                virtualThreadId: 0,
                [NativeTransportRuntimeAdapter.ToCore(in instruction)]));

        Assert.Contains(expectedReason, exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("fail closed", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void StreamVectorContourProviderRejectsLoweringWithoutScalarFallback()
    {
        CompilerSemanticIntent intent = new CompilerSemanticIntent(
            SemanticIntentKind.VectorStream,
            "VLOAD/VSTORE",
            RequiresDescriptor: false,
            RequiresSideband: false,
            RequiresToken: false,
            RequiresRuntimeLegality: true,
            IsCompatibilityProjection: false,
            IsPolicyAdmissionOnly: false,
            IsHelperAbiOnly: true,
            IsParserOnly: false,
            "Phase09 Stream/vector helper-only negative gate.");
        CompilerLoweringContext context = new CompilerLoweringContext(
            new CompilerTargetProfile("phase09-stream-vector"),
            "CompilerPhase09StreamVectorNegativeMatrixTests");

        IContourAnalyzer analyzer =
            DefaultContourLoweringProviderRegistry.Instance.ResolveAnalyzer(ExecutionContourKind.StreamEngineVector);
        ContourAnalysisReport analysis = analyzer.Analyze(intent, context);
        IContourLoweringProvider provider =
            DefaultContourLoweringProviderRegistry.Instance.ResolveProvider(ExecutionContourKind.StreamEngineVector);
        CompilerLoweringDecision decision = provider.Lower(intent, analysis, context);

        Assert.True(analysis.ProviderAvailable);
        Assert.Equal(ExecutionContourKind.StreamEngineVector, analysis.ContourKind);
        Assert.Equal(CompilerCapabilityObservationState.HelperOnly, analysis.CapabilityObservation.State);
        Assert.Contains("scalar fallback is forbidden", analysis.CapabilityObservation.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(CompilerLoweringDecisionKind.Rejected, decision.DecisionKind);
        Assert.Equal(CompilerRejectReason.RuntimeAuthorityOwned, Assert.Single(decision.RejectReasons));
        Assert.Equal(CompilerEmissionClass.NoEmission, decision.EmissionClass);
        Assert.False(decision.FallbackPolicy.AllowsCrossContourFallback);
        Assert.Equal(FallbackPolicyKind.Forbidden, decision.NoFallbackProof.PolicyKind);
    }
}
