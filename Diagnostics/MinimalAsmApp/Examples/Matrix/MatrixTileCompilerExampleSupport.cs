using HybridCPU.Compiler.Core.API.Facade;
using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.Runtime;
using HybridCPU.Compiler.Core.Threading;
using HybridCPU_ISE.Arch;
using MinimalAsmApp.Examples.Abstractions;
using YAKSys_Hybrid_CPU;
using YAKSys_Hybrid_CPU.CloseToHSL.Core.ISA.Instructions.NonVmx.Lanes00_03Vector.MatrixTile;

namespace MinimalAsmApp.Examples.Vector;

using CompilerMatrixTileNumericPolicy = HybridCPU.Compiler.Core.IR.Matrix.MatrixTileNumericPolicy;
using Instruction = Processor.CPU_Core.InstructionsEnum;

internal static class MatrixTileCompilerExampleSupport
{
    private static readonly MatrixTileCanonicalDescriptorAbi Tile2X2I8 =
        MatrixTileCanonicalDescriptorAbi.Create(
            rows: 2,
            columns: 2,
            elementSizeBytes: 1,
            strideBytes: 2);

#pragma warning disable CS0618
    public static CpuExampleResult Run(
        string output,
        string helperName,
        Instruction expectedOpcode,
        Action<AppAsmFacade> emit,
        MatrixTileNumericPolicy? expectedNumericPolicy,
        MatrixTileLayoutPolicy? expectedLayoutPolicy)
    {
        HybridCpuThreadCompilerContext context = new HybridCpuThreadCompilerContext(virtualThreadId: 0);
        AppAsmFacade facade = new AppAsmFacade(0, context);

        emit(facade);

        VLIW_Instruction[] sourceInstructions = context.GetCompiledInstructions().ToArray();
        if (sourceInstructions.Length != 1)
        {
            throw new InvalidOperationException(
                $"Expected one {expectedOpcode} source instruction, got {sourceInstructions.Length}.");
        }

        VLIW_Instruction sourceInstruction = sourceInstructions[0];
        if ((Instruction)sourceInstruction.OpCode != expectedOpcode)
        {
            throw new InvalidOperationException(
                $"Expected source opcode {expectedOpcode}, got {(Instruction)sourceInstruction.OpCode}.");
        }

        if (!context.GetBundleAnnotations()
                .TryGetInstructionSlotMetadata(0, out InstructionSlotMetadata sourceMetadata))
        {
            throw new InvalidOperationException(
                $"Expected source metadata for {expectedOpcode}.");
        }

        AssertExpectedSidebands(
            expectedOpcode,
            sourceMetadata,
            expectedNumericPolicy,
            expectedLayoutPolicy,
            "source");

        HybridCpuCompiledProgram compiledProgram = context.CompileProgram();
        (VLIW_Instruction loweredInstruction, InstructionSlotMetadata loweredMetadata) =
            FindLoweredMatrixTileSlot(compiledProgram, expectedOpcode);
        AssertExpectedSidebands(
            expectedOpcode,
            loweredMetadata,
            expectedNumericPolicy,
            expectedLayoutPolicy,
            "lowered");

        return CpuExampleResult.Ok(
            output,
            notes:
            [
                $"{expectedOpcode} emitted via {helperName}.",
                $"source opcode={(Instruction)sourceInstruction.OpCode}, lowered opcode={(Instruction)loweredInstruction.OpCode}",
                $"source sidebands: {DescribeSidebands(sourceMetadata)}",
                $"lowered sidebands: {DescribeSidebands(loweredMetadata)}",
                $"canonical compile accepted {compiledProgram.BundleLayout.Program.Instructions.Count} instruction into {compiledProgram.BundleCount} lowered bundle(s)",
                $"typed-slot agreement valid = {compiledProgram.AdmissibilityAgreement.AllTypedSlotFactsValid}"
            ]);
    }
#pragma warning restore CS0618

    public static CompilerMatrixTileDescriptorAbi CreateDescriptor(
        DataTypeEnum elementType = DataTypeEnum.INT8) =>
        new(
            NativeTransportRuntimeAdapter.ToCore(Tile2X2I8),
            NativeTransportRuntimeAdapter.ToCore(elementType));

    public static CompilerMatrixTileMemoryFaultAbiInputs CreateMemoryFault(
        ulong baseAddress = 0x7000) =>
        CompilerMatrixTileMemoryFaultAbiInputs.Create(baseAddress);

    public static CompilerMatrixTileAccumulatorPolicyAbi CreateMaccPolicy()
    {
        CompilerMatrixTileNumericPolicy numericPolicy =
            CompilerMatrixTilePositiveEmissionAbiContract.CreateRuntimeNumericPolicy(
                NativeTransportRuntimeAdapter.ToCore(DataTypeEnum.INT8));
        return CompilerMatrixTileAccumulatorPolicyAbi.CreateForRuntimeDerivedFootprint(
            NativeTransportRuntimeAdapter.ToCore(Tile2X2I8),
            numericPolicy);
    }

    public static CompilerMatrixTileTransposePolicyAbi CreateTransposePolicy() =>
        CompilerMatrixTileTransposePolicyAbi.CreateForRuntimeDerivedDestination(
            NativeTransportRuntimeAdapter.ToCore(Tile2X2I8));

    public static MatrixTileNumericPolicy CreateExpectedMaccNumericPolicy() =>
        NativeTransportRuntimeAdapter.ToRuntime(
            CompilerMatrixTilePositiveEmissionAbiContract.CreateRuntimeNumericPolicy(
                NativeTransportRuntimeAdapter.ToCore(DataTypeEnum.INT8)));

    public static MatrixTileLayoutPolicy CreateExpectedMaccLayoutPolicy() =>
        MatrixTileLayoutPolicyAbi.CreateMaccPolicy();

    public static MatrixTileLayoutPolicy CreateExpectedTransposeLayoutPolicy() =>
        MatrixTileLayoutPolicyAbi.CreateTransposePolicy();

    private static void AssertExpectedSidebands(
        Instruction opcode,
        InstructionSlotMetadata metadata,
        MatrixTileNumericPolicy? expectedNumericPolicy,
        MatrixTileLayoutPolicy? expectedLayoutPolicy,
        string surface)
    {
        if (!Nullable.Equals(metadata.MatrixTileNumericPolicy, expectedNumericPolicy))
        {
            throw new InvalidOperationException(
                $"{opcode}: unexpected {surface} MatrixTileNumericPolicy sideband.");
        }

        if (!Nullable.Equals(metadata.MatrixTileLayoutPolicy, expectedLayoutPolicy))
        {
            throw new InvalidOperationException(
                $"{opcode}: unexpected {surface} MatrixTileLayoutPolicy sideband.");
        }
    }

    private static (VLIW_Instruction Instruction, InstructionSlotMetadata Metadata)
        FindLoweredMatrixTileSlot(
            HybridCpuCompiledProgram compiledProgram,
            Instruction opcode)
    {
        for (int bundleIndex = 0; bundleIndex < compiledProgram.LoweredBundles.Count; bundleIndex++)
        {
            HybridCpuInstructionBundle compilerBundle = compiledProgram.LoweredBundles[bundleIndex];
            VLIW_Bundle bundle = NativeTransportRuntimeAdapter.ToRuntime(in compilerBundle);
            VliwBundleAnnotations annotations = NativeTransportRuntimeAdapter.ToRuntime(
                compiledProgram.LoweredBundleAnnotations[bundleIndex]);
            for (int slotIndex = 0; slotIndex < YAKSys_Hybrid_CPU.Core.BundleMetadata.BundleSlotCount; slotIndex++)
            {
                VLIW_Instruction instruction = bundle.GetInstruction(slotIndex);
                if ((Instruction)instruction.OpCode != opcode)
                {
                    continue;
                }

                if (!annotations.TryGetInstructionSlotMetadata(slotIndex, out InstructionSlotMetadata metadata))
                {
                    throw new InvalidOperationException(
                        $"Expected lowered metadata for {opcode}.");
                }

                return (instruction, metadata);
            }
        }

        throw new InvalidOperationException($"Expected lowered {opcode} instruction.");
    }

    private static string DescribeSidebands(InstructionSlotMetadata metadata)
    {
        string numeric = metadata.MatrixTileNumericPolicy is { } numericPolicy
            ? $"{numericPolicy.ProfileId}/{numericPolicy.Fingerprint:X16}"
            : "none";
        string layout = metadata.MatrixTileLayoutPolicy is { } layoutPolicy
            ? $"{layoutPolicy.OperationKind}/{layoutPolicy.Fingerprint:X16}"
            : "none";
        return $"numeric={numeric}, layout={layout}";
    }
}
