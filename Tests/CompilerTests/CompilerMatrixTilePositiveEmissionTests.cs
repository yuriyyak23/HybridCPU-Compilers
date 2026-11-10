using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HybridCPU.Compiler.Core;
using HybridCPU.Compiler.Core.API.Facade;
using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.Runtime;
using HybridCPU.Compiler.Core.Threading;
using HybridCPU_ISE.Arch;
using HybridCPU_ISE.Tests.TestHelpers;
using Xunit;
using YAKSys_Hybrid_CPU;
using YAKSys_Hybrid_CPU.Arch;
using YAKSys_Hybrid_CPU.CloseToHSL.Core.ISA.Instructions.NonVmx.Lanes00_03Vector.MatrixTile;
using YAKSys_Hybrid_CPU.Core;
using YAKSys_Hybrid_CPU.Core.Pipeline.MicroOps;
using static YAKSys_Hybrid_CPU.Processor.CPU_Core;
using MatrixTileCanonicalDescriptorAbi = HybridCPU.Compiler.Core.IR.Matrix.MatrixTileCanonicalDescriptorAbi;
using MatrixTileLayoutPolicy = HybridCPU.Compiler.Core.IR.Matrix.MatrixTileLayoutPolicy;
using MatrixTileNumericPolicy = HybridCPU.Compiler.Core.IR.Matrix.MatrixTileNumericPolicy;
using RuntimeMatrixTileLayoutPolicy = YAKSys_Hybrid_CPU.CloseToHSL.Core.ISA.Instructions.NonVmx.Lanes00_03Vector.MatrixTile.MatrixTileLayoutPolicy;
using RuntimeMatrixTileNumericPolicy = YAKSys_Hybrid_CPU.CloseToHSL.Core.ISA.Instructions.NonVmx.Lanes00_03Vector.MatrixTile.MatrixTileNumericPolicy;
using RuntimeMatrixTileInstructionIrProjection = YAKSys_Hybrid_CPU.CloseToHSL.Core.ISA.Instructions.NonVmx.Lanes00_03Vector.MatrixTile.MatrixTileInstructionIrProjection;

namespace HybridCPU_ISE.Tests.CompilerTests;

public sealed class CompilerMatrixTilePositiveEmissionTests
{
    private static readonly CompilerMatrixTileDescriptorAbi Tile2X2I8 =
        new(MatrixTileCanonicalDescriptorAbi.Create(2, 2, 1, 2), HybridCpuDataType.INT8);

    public static IEnumerable<object[]> ExecutionGoldenVectors()
    {
        MatrixTileExecutionGoldenVector[] vectors =
            MatrixTilePositiveGoldenArtifactManifest.ExecutionVectors.ToArray();
        foreach (MatrixTileExecutionGoldenVector vector in vectors)
        {
            yield return [vector];
        }
    }

    [Theory]
    [MemberData(nameof(ExecutionGoldenVectors))]
    public void FacadeHelpers_EmitDirectMatrixTileWithRuntimePolicySidebands(
        MatrixTileExecutionGoldenVector vector)
    {
        HybridCpuThreadCompilerContext context = new HybridCpuThreadCompilerContext(virtualThreadId: 0);

#pragma warning disable CS0618
        AppAsmFacade facade = new AppAsmFacade(0, context);
#pragma warning restore CS0618

        EmitGoldenVector(facade, vector);

        Assert.Equal(1, context.InstructionCount);
        VLIW_Instruction sourceInstruction = context.GetCompiledInstructions()[0];
        AssertCarrier(vector.Carrier, sourceInstruction);

        VliwBundleAnnotations sourceAnnotations = context.GetBundleAnnotations();
        Assert.True(sourceAnnotations.TryGetInstructionSlotMetadata(0, out InstructionSlotMetadata sourceMetadata));
        AssertMatrixTilePolicySidebands(vector, sourceMetadata);
        AssertRuntimeProjectionAccepts(vector, sourceInstruction, sourceMetadata);

        HybridCpuCompiledProgram compiledProgram = context.CompileProgram();
        (VLIW_Instruction loweredInstruction, InstructionSlotMetadata loweredMetadata) =
            FindLoweredMatrixTileSlot(compiledProgram, vector.Opcode);
        AssertCarrier(vector.Carrier, loweredInstruction);
        AssertMatrixTilePolicySidebands(vector, loweredMetadata);
        AssertRuntimeProjectionAccepts(vector, loweredInstruction, loweredMetadata);
    }

    [Fact]
    public void MalformedHelperInputs_FailClosedBeforeEmission()
    {
        HybridCpuThreadCompilerContext context = new HybridCpuThreadCompilerContext(virtualThreadId: 0);

#pragma warning disable CS0618
        AppAsmFacade facade = new AppAsmFacade(0, context);
#pragma warning restore CS0618

        CompilerMatrixTileDescriptorAbi nonCanonicalDescriptor =
            new CompilerMatrixTileDescriptorAbi(
                MatrixTileCanonicalDescriptorAbi.Create(0, 2, 1, 2),
                HybridCpuDataType.INT8);
        Assert.Throws<ArgumentException>(() =>
            facade.MtileLoad(
                CompilerMatrixTileTileOperand.Create(1),
                nonCanonicalDescriptor,
                CompilerMatrixTileMemoryFaultAbiInputs.Create(0x100)));
        Assert.Equal(0, context.InstructionCount);

        ArgumentException shapeException = Assert.Throws<ArgumentException>(() =>
            facade.MtileLoad(
                CompilerMatrixTileTileOperand.Create(1),
                Tile2X2I8,
                CompilerMatrixTileMemoryFaultAbiInputs.Create(0x100, pageSizeBytes: 2048)));
        Assert.Contains("default page-size", shapeException.Message, StringComparison.Ordinal);
        Assert.Equal(0, context.InstructionCount);

        CompilerMatrixTileAccumulatorPolicyAbi malformedAccumulatorPolicy =
            new CompilerMatrixTileAccumulatorPolicyAbi(
                Tile2X2I8.CanonicalDescriptor,
                Tile2X2I8.CanonicalDescriptor,
                (HybridCPU.Compiler.Core.IR.Matrix.MatrixTileNumericElementKind)(byte)MatrixTileNumericElementKind.SignedInteger,
                (HybridCPU.Compiler.Core.IR.Matrix.MatrixTileAccumulatorPolicyKind)(byte)MatrixTileAccumulatorPolicyKind.WideningIntegerAccumulatorWithOverflowTrap);
        ArgumentException accumulatorException = Assert.Throws<ArgumentException>(() =>
            facade.MtileMacc(
                CompilerMatrixTileTileOperand.Create(1),
                CompilerMatrixTileTileOperand.Create(2),
                CompilerMatrixTileTileOperand.Create(3),
                Tile2X2I8,
                malformedAccumulatorPolicy));
        Assert.Contains("MatrixTileNumericPolicy", accumulatorException.Message, StringComparison.Ordinal);
        Assert.Equal(0, context.InstructionCount);

        CompilerMatrixTileDescriptorAbi twoByThree =
            new CompilerMatrixTileDescriptorAbi(
                MatrixTileCanonicalDescriptorAbi.Create(2, 3, 1, 3),
                HybridCpuDataType.INT8);
        CompilerMatrixTileTransposePolicyAbi inPlaceNonSquareTranspose =
            new CompilerMatrixTileTransposePolicyAbi(
                MatrixTileCanonicalDescriptorAbi.Create(3, 2, 1, 2),
                (HybridCPU.Compiler.Core.IR.Matrix.MatrixTileTransposeAliasPolicyKind)(byte)MatrixTileTransposeAliasPolicyKind.OutOfPlaceOrSquareInPlaceOnly)
            {
                MatrixTileLayoutPolicy = NativeTransportRuntimeAdapter.ToCore(MatrixTileLayoutPolicyAbi.CreateTransposePolicy())
            };
        ArgumentException transposeException = Assert.Throws<ArgumentException>(() =>
            facade.Mtranspose(
                CompilerMatrixTileTileOperand.Create(1),
                CompilerMatrixTileTileOperand.Create(1),
                twoByThree,
                inPlaceNonSquareTranspose));
        Assert.Contains("TransposeInPlaceRequiresSquareShape", transposeException.Message, StringComparison.Ordinal);
        Assert.Equal(0, context.InstructionCount);
    }

    [Fact]
    public void RuntimeProjectionRejectsMissingTamperedAndUnsupportedCompilerSidebands()
    {
        MatrixTileExecutionGoldenVector macc =
            MatrixTilePositiveGoldenArtifactManifest.ExecutionVectors.ToArray().Single(
                static vector => vector.Opcode == InstructionsEnum.MTILE_MACC);
        VLIW_Instruction instruction = EmitSingleInstruction(macc, out InstructionSlotMetadata metadata);
        RuntimeMatrixTileNumericPolicy numericPolicy = metadata.MatrixTileNumericPolicy!.Value;
        RuntimeMatrixTileLayoutPolicy layoutPolicy = metadata.MatrixTileLayoutPolicy!.Value;

        MatrixTileInstructionIrProjection missingLayout =
            ProjectWithSidebands(instruction, macc.Opcode, numericPolicy, matrixTileLayoutPolicy: null);
        Assert.Equal(MatrixTileIrProjectionFaultKind.LayoutPolicyFault, missingLayout.FaultKind);
        Assert.Equal(MatrixTileLayoutPolicyFaultKind.MissingPolicy, missingLayout.LayoutPolicyValidation!.Value.FaultKind);

        MatrixTileInstructionIrProjection missingNumeric =
            ProjectWithSidebands(instruction, macc.Opcode, matrixTileNumericPolicy: null, layoutPolicy);
        Assert.Equal(MatrixTileIrProjectionFaultKind.NumericPolicyFault, missingNumeric.FaultKind);
        Assert.Equal(MatrixTileNumericPolicyFaultKind.MissingPolicy, missingNumeric.NumericPolicyValidation!.Value.FaultKind);

        RuntimeMatrixTileNumericPolicy tamperedNumeric =
            numericPolicy with { Fingerprint = numericPolicy.Fingerprint + 1 };
        MatrixTileInstructionIrProjection tamperedNumericProjection =
            ProjectWithSidebands(instruction, macc.Opcode, tamperedNumeric, layoutPolicy);
        Assert.Equal(MatrixTileIrProjectionFaultKind.NumericPolicyFault, tamperedNumericProjection.FaultKind);
        Assert.Equal(
            MatrixTileNumericPolicyFaultKind.FingerprintMismatch,
            tamperedNumericProjection.NumericPolicyValidation!.Value.FaultKind);

        RuntimeMatrixTileNumericPolicy unsupportedNumeric =
            numericPolicy with { AbiVersion = MatrixTileNumericPolicyAbi.CurrentAbiVersion + 1 };
        MatrixTileInstructionIrProjection unsupportedNumericProjection =
            ProjectWithSidebands(instruction, macc.Opcode, unsupportedNumeric, layoutPolicy);
        Assert.Equal(MatrixTileIrProjectionFaultKind.NumericPolicyFault, unsupportedNumericProjection.FaultKind);
        Assert.Equal(
            MatrixTileNumericPolicyFaultKind.UnsupportedAbiVersion,
            unsupportedNumericProjection.NumericPolicyValidation!.Value.FaultKind);

        MatrixTileExecutionGoldenVector transpose =
            MatrixTilePositiveGoldenArtifactManifest.ExecutionVectors.ToArray().Single(
                static vector => vector.Opcode == InstructionsEnum.MTRANSPOSE);
        VLIW_Instruction transposeInstruction = EmitSingleInstruction(transpose, out _);
        MatrixTileInstructionIrProjection wrongTransposeLayout =
            ProjectWithSidebands(
                transposeInstruction,
                transpose.Opcode,
                matrixTileNumericPolicy: null,
                MatrixTileLayoutPolicyAbi.CreateMaccPolicy());
        Assert.Equal(MatrixTileIrProjectionFaultKind.LayoutPolicyFault, wrongTransposeLayout.FaultKind);
        Assert.Equal(
            MatrixTileLayoutPolicyFaultKind.UnsupportedOperation,
            wrongTransposeLayout.LayoutPolicyValidation!.Value.FaultKind);
    }

    [Fact]
    public void RawMatrixTileIngress_IsRejectedInFavorOfTypedHelperAbi()
    {
        HybridCpuThreadCompilerContext context = new HybridCpuThreadCompilerContext(virtualThreadId: 0);

        InvalidOperationException directException = Assert.Throws<InvalidOperationException>(() =>
            context.CompileInstruction(
                (uint)InstructionsEnum.MTILE_LOAD,
                (byte)DataTypeEnum.INT8,
                predicate: 0,
                immediate: 2,
                destSrc1: 0x100,
                src2: 2,
                streamLength: 4,
                stride: 1,
                stealabilityPolicy: StealabilityPolicy.NotStealable));
        Assert.Contains("typed matrix/tile helper ABI", directException.Message, StringComparison.Ordinal);
        Assert.Equal(0, context.InstructionCount);

#pragma warning disable CS0618
        ExpertBackendFacade expert = new ExpertBackendFacade(0, context);
#pragma warning restore CS0618
        InvalidOperationException expertException = Assert.Throws<InvalidOperationException>(() =>
            expert.EmitRawInstruction(
                (uint)InstructionsEnum.MTILE_STORE,
                (byte)DataTypeEnum.INT8,
                predicate: 0,
                immediate: 2,
                destSrc1: 0x180,
                src2: 2,
                streamLength: 4,
                stride: 1));
        Assert.Contains("typed matrix/tile helper ABI", expertException.Message, StringComparison.Ordinal);
        Assert.Equal(0, context.InstructionCount);
    }

    [Fact]
    public void MatrixTileCompilerOwnedSources_DoNotReferenceFallbackOrAliasOpcodeFamilies()
    {
        string source = ReadCompilerOwnedMatrixTileSource();
        string[] forbiddenFragments =
        [
            "InstructionsEnum.VLOAD",
            "InstructionsEnum.VSTORE",
            "InstructionsEnum.VDOT",
            "InstructionsEnum.VTRANSPOSE",
            "InstructionsEnum.FMAC",
            "InstructionsEnum.DmaStreamCompute",
            "CompileVector",
            "CompileVdot",
            "CompileDmaStreamCompute",
            "CompileAccelerator",
            "EmitRawInstruction(",
            "ExpertBackendFacade",
            "ExternalBackend",
            ".ISA.Instructions.Vmx."
        ];

        foreach (string forbiddenFragment in forbiddenFragments)
        {
            Assert.DoesNotContain(forbiddenFragment, source, StringComparison.Ordinal);
        }

        Assert.Contains(nameof(HybridCpuThreadCompilerContext.CompileMtileLoad), source);
        Assert.Contains(nameof(HybridCpuThreadCompilerContext.CompileMtileStore), source);
        Assert.Contains(nameof(HybridCpuThreadCompilerContext.CompileMtileMacc), source);
        Assert.Contains(nameof(HybridCpuThreadCompilerContext.CompileMtranspose), source);
        Assert.Contains(CompilerMatrixTilePositiveEmissionAbiContract.NoFallbackDecision, source);
        Assert.Contains(
            nameof(CompilerMatrixTilePositiveEmissionAbiContract.CarriesRuntimeOwnedMatrixTilePolicySidebands),
            source);
        Assert.Contains("MatrixTileNumericPolicy", source, StringComparison.Ordinal);
        Assert.Contains("MatrixTileLayoutPolicy", source, StringComparison.Ordinal);
    }

    private static void EmitGoldenVector(
        AppAsmFacade facade,
        MatrixTileExecutionGoldenVector vector)
    {
        CompilerMatrixTileDescriptorAbi descriptor =
            new(NativeTransportRuntimeAdapter.ToCore(vector.TileDescriptor), HybridCpuDataType.INT8);
        switch (vector.Opcode)
        {
            case InstructionsEnum.MTILE_LOAD:
                facade.MtileLoad(
                    CompilerMatrixTileTileOperand.Create(vector.DestinationTileId),
                    descriptor,
                    CompilerMatrixTileMemoryFaultAbiInputs.Create(vector.Carrier.Word1));
                break;
            case InstructionsEnum.MTILE_STORE:
                facade.MtileStore(
                    CompilerMatrixTileTileOperand.Create(vector.SourceTileId),
                    descriptor,
                    CompilerMatrixTileMemoryFaultAbiInputs.Create(vector.Carrier.Word1));
                break;
            case InstructionsEnum.MTILE_MACC:
                facade.MtileMacc(
                    CompilerMatrixTileTileOperand.Create(vector.SourceTileId),
                    CompilerMatrixTileTileOperand.Create(vector.SecondaryTileId),
                    CompilerMatrixTileTileOperand.Create(vector.DestinationTileId),
                    descriptor,
                    CreateAccumulatorPolicyAbi(
                        descriptor.ElementType,
                        NativeTransportRuntimeAdapter.ToCore(vector.SecondaryTileDescriptor),
                        NativeTransportRuntimeAdapter.ToCore(vector.ResultTileDescriptor)));
                break;
            case InstructionsEnum.MTRANSPOSE:
                facade.Mtranspose(
                    CompilerMatrixTileTileOperand.Create(vector.SourceTileId),
                    CompilerMatrixTileTileOperand.Create(vector.DestinationTileId),
                    descriptor,
                    new CompilerMatrixTileTransposePolicyAbi(
                        NativeTransportRuntimeAdapter.ToCore(vector.ResultTileDescriptor),
                        (HybridCPU.Compiler.Core.IR.Matrix.MatrixTileTransposeAliasPolicyKind)(byte)MatrixTileTransposeAliasPolicyKind.OutOfPlaceOrSquareInPlaceOnly)
                    {
                        MatrixTileLayoutPolicy = NativeTransportRuntimeAdapter.ToCore(MatrixTileLayoutPolicyAbi.CreateTransposePolicy())
                    });
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(vector), vector.Opcode, "Unsupported MTILE golden vector.");
        }
    }

    private static void AssertMatrixTileOperandContract(
        IrInstruction ir,
        MatrixTileExecutionGoldenVector vector)
    {
        switch (vector.Opcode)
        {
            case InstructionsEnum.MTILE_LOAD:
                Assert.Contains(ir.Operands, operand => operand.Kind == IrOperandKind.MemoryAddress && operand.Name == "tileMemoryBase");
                Assert.Contains(ir.Annotation.Defs, operand => operand.Kind == IrOperandKind.Tile && operand.Name == "tileDestination");
                Assert.Contains(ir.Annotation.Uses, operand => operand.Kind == IrOperandKind.MemoryAddress && operand.Name == "tileMemoryBase");
                Assert.Null(ir.Annotation.MemoryWriteRegion);
                Assert.NotNull(ir.Annotation.MemoryReadRegion);
                break;
            case InstructionsEnum.MTILE_STORE:
                Assert.Contains(ir.Operands, operand => operand.Kind == IrOperandKind.Tile && operand.Name == "tileSource");
                Assert.Empty(ir.Annotation.Defs);
                Assert.Contains(ir.Annotation.Uses, operand => operand.Kind == IrOperandKind.Tile && operand.Name == "tileSource");
                Assert.Null(ir.Annotation.MemoryReadRegion);
                Assert.NotNull(ir.Annotation.MemoryWriteRegion);
                break;
            case InstructionsEnum.MTILE_MACC:
                Assert.Contains(ir.Annotation.Defs, operand => operand.Kind == IrOperandKind.Tile && operand.Name == "tileAccumulator");
                Assert.Contains(ir.Annotation.Uses, operand => operand.Kind == IrOperandKind.Tile && operand.Name == "tileLeftSource");
                Assert.Contains(ir.Annotation.Uses, operand => operand.Kind == IrOperandKind.Tile && operand.Name == "tileRightSource");
                Assert.Null(ir.Annotation.MemoryReadRegion);
                Assert.Null(ir.Annotation.MemoryWriteRegion);
                break;
            case InstructionsEnum.MTRANSPOSE:
                Assert.Contains(ir.Annotation.Defs, operand => operand.Kind == IrOperandKind.Tile && operand.Name == "tileDestination");
                Assert.Contains(ir.Annotation.Uses, operand => operand.Kind == IrOperandKind.Tile && operand.Name == "tileSource");
                Assert.Null(ir.Annotation.MemoryReadRegion);
                Assert.Null(ir.Annotation.MemoryWriteRegion);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(vector), vector.Opcode, "Unsupported MTILE golden vector.");
        }
    }

    private static void AssertCarrier(
        MatrixTileGoldenCarrier expected,
        VLIW_Instruction actual)
    {
        Assert.Equal(expected.Word0, actual.Word0);
        Assert.Equal(expected.Word1, actual.Word1);
        Assert.Equal(expected.Word2, actual.Word2);
        Assert.Equal(expected.Word3, actual.Word3);
    }

    private static CompilerMatrixTileAccumulatorPolicyAbi CreateAccumulatorPolicyAbi(
        HybridCpuDataType elementType,
        MatrixTileCanonicalDescriptorAbi secondaryTileDescriptor,
        MatrixTileCanonicalDescriptorAbi resultTileDescriptor)
    {
        MatrixTileNumericPolicy numericPolicy =
            CompilerMatrixTilePositiveEmissionAbiContract.CreateRuntimeNumericPolicy(elementType);
        return new CompilerMatrixTileAccumulatorPolicyAbi(
            secondaryTileDescriptor,
            resultTileDescriptor,
            CompilerMatrixTilePositiveEmissionAbiContract.GetElementKind(numericPolicy),
            CompilerMatrixTilePositiveEmissionAbiContract.GetAccumulatorPolicy(numericPolicy))
        {
            MatrixTileNumericPolicy = numericPolicy,
            MatrixTileLayoutPolicy = NativeTransportRuntimeAdapter.ToCore(MatrixTileLayoutPolicyAbi.CreateMaccPolicy())
        };
    }

    private static void AssertMatrixTilePolicySidebands(
        MatrixTileExecutionGoldenVector vector,
        InstructionSlotMetadata metadata)
    {
        switch (vector.Opcode)
        {
            case InstructionsEnum.MTILE_LOAD:
            case InstructionsEnum.MTILE_STORE:
                Assert.Null(metadata.MatrixTileNumericPolicy);
                Assert.Null(metadata.MatrixTileLayoutPolicy);
                break;
            case InstructionsEnum.MTILE_MACC:
                RuntimeMatrixTileNumericPolicy expectedNumeric = NativeTransportRuntimeAdapter.ToRuntime(
                    CompilerMatrixTilePositiveEmissionAbiContract.CreateRuntimeNumericPolicy(
                        HybridCpuDataType.INT8));
                Assert.Equal(expectedNumeric, metadata.MatrixTileNumericPolicy);
                Assert.Equal(MatrixTileLayoutPolicyAbi.CreateMaccPolicy(), metadata.MatrixTileLayoutPolicy);
                break;
            case InstructionsEnum.MTRANSPOSE:
                Assert.Null(metadata.MatrixTileNumericPolicy);
                Assert.Equal(MatrixTileLayoutPolicyAbi.CreateTransposePolicy(), metadata.MatrixTileLayoutPolicy);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(vector), vector.Opcode, "Unsupported MTILE golden vector.");
        }
    }

    private static void AssertRuntimeProjectionAccepts(
        MatrixTileExecutionGoldenVector vector,
        VLIW_Instruction instruction,
        InstructionSlotMetadata metadata)
    {
        RuntimeMatrixTileInstructionIrProjection projection =
            ProjectWithSidebands(
                instruction,
                vector.Opcode,
                metadata.MatrixTileNumericPolicy,
                metadata.MatrixTileLayoutPolicy);
        Assert.Equal(MatrixTileIrProjectionFaultKind.None, projection.FaultKind);
        Assert.True(projection.SemanticValidation?.IsSemanticAbiAccepted ?? true);
        Assert.True(MatrixTileIrProjectionAndMaterializer.TryMaterialize(
            projection,
            out MatrixTileMaterializedInstruction materialized,
            out MatrixTileIrProjectionFaultKind faultKind));
        Assert.Equal(MatrixTileIrProjectionFaultKind.None, faultKind);
        Assert.Equal(vector.OperationKind, materialized.OperationKind);
    }

    private static RuntimeMatrixTileInstructionIrProjection ProjectWithSidebands(
        VLIW_Instruction instruction,
        InstructionsEnum opcode,
        RuntimeMatrixTileNumericPolicy? matrixTileNumericPolicy,
        RuntimeMatrixTileLayoutPolicy? matrixTileLayoutPolicy)
    {
        VectorInstructionPayload payload = new VectorInstructionPayload(
            instruction.DestSrc1Pointer,
            instruction.Src2Pointer,
            instruction.StreamLength,
            instruction.Stride,
            instruction.RowStride,
            instruction.Indexed,
            instruction.Is2D,
            instruction.TailAgnostic,
            instruction.MaskAgnostic,
            instruction.Saturating,
            instruction.PredicateMask,
            instruction.DataType)
        {
            MatrixTileNumericPolicy = matrixTileNumericPolicy,
            MatrixTileLayoutPolicy = matrixTileLayoutPolicy
        };

        return MatrixTileIrProjectionAndMaterializer.ProjectDecodedVectorPayload(
            opcode,
            payload,
            instruction.Immediate,
            requireExplicitNumericPolicy: true);
    }

    private static VLIW_Instruction EmitSingleInstruction(
        MatrixTileExecutionGoldenVector vector,
        out InstructionSlotMetadata metadata)
    {
        HybridCpuThreadCompilerContext context = new HybridCpuThreadCompilerContext(virtualThreadId: 0);

#pragma warning disable CS0618
        AppAsmFacade facade = new AppAsmFacade(0, context);
#pragma warning restore CS0618

        EmitGoldenVector(facade, vector);
        Assert.True(context.GetBundleAnnotations().TryGetInstructionSlotMetadata(0, out metadata));
        return context.GetCompiledInstructions()[0];
    }

    private static (VLIW_Instruction Instruction, InstructionSlotMetadata Metadata) FindLoweredMatrixTileSlot(
        HybridCpuCompiledProgram compiledProgram,
        InstructionsEnum opcode)
    {
        for (int bundleIndex = 0; bundleIndex < compiledProgram.LoweredBundles.Count; bundleIndex++)
        {
            HybridCpuInstructionBundle coreBundle = compiledProgram.LoweredBundles[bundleIndex];
            VLIW_Bundle bundle = NativeTransportRuntimeAdapter.ToRuntime(in coreBundle);
            VliwBundleAnnotations annotations = NativeTransportRuntimeAdapter.ToRuntime(
                compiledProgram.LoweredBundleAnnotations[bundleIndex]);
            for (int slotIndex = 0; slotIndex < BundleMetadata.BundleSlotCount; slotIndex++)
            {
                VLIW_Instruction instruction = bundle.GetInstruction(slotIndex);
                if ((InstructionsEnum)instruction.OpCode != opcode)
                {
                    continue;
                }

                Assert.True(annotations.TryGetInstructionSlotMetadata(slotIndex, out InstructionSlotMetadata metadata));
                return (instruction, metadata);
            }
        }

        throw new InvalidOperationException($"Expected lowered {opcode} instruction.");
    }

    private static string ReadCompilerOwnedMatrixTileSource()
    {
        string[] ownedFileNames =
        [
            "CompilerMatrixTilePositiveEmissionAbiContract.cs",
            "CompilerMatrixTileEmissionLowerer.cs",
            "ThreadCompilerContext.MatrixTile.cs"
        ];

        return string.Join(
            Environment.NewLine,
            CompilerSourceScanner.EnumerateCompilerSourceFiles()
                .Where(path => ownedFileNames.Contains(Path.GetFileName(path), StringComparer.Ordinal))
                .OrderBy(path => path, StringComparer.Ordinal)
                .Select(File.ReadAllText));
    }
}
