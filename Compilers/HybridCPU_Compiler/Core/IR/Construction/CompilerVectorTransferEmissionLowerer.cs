using System;
using HybridCPU.Compiler.Core.IR.Contours;
using HybridCPU.Compiler.Core.IR.Intent;
using HybridCPU.Compiler.Core.IR.Lowering;

namespace HybridCPU.Compiler.Core.IR;

internal static class CompilerVectorTransferEmissionLowerer
{
    private const string RecoverySourceApi =
        "CompilerVectorTransferEmissionLowerer.TryRecoverFromInstruction";

    private const string PositiveEmissionSourceApi =
        "CompilerVectorTransferEmissionLowerer.Lower";

    [Obsolete(
        "Lower returns a raw VectorTransfer plan artifact. Use LowerWithDecision so positive helper carrier emission carries CompilerLoweringDecision no-authority metadata.",
        false)]
    public static CompilerVectorTransferEmissionPlan Lower(CompilerVectorTransferEmissionRequest request)
    {
        return LowerWithDecision(request).Plan;
    }

    public static CompilerPositiveEmissionResult<CompilerVectorTransferEmissionPlan> LowerWithDecision(
        CompilerVectorTransferEmissionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);

        HybridCpuInstructionWord instruction = BuildInstruction(request);
        CompilerVectorTransferEmissionPlan plan = CreatePlan(request, instruction);
        string reason =
            $"{request.Mnemonic} VectorTransfer helper carrier was produced as helper/transport ABI only; runtime legality, execution, publication, commit and retire remain runtime-owned.";
        return new CompilerPositiveEmissionResult<CompilerVectorTransferEmissionPlan>(
            CreatePositiveEmissionDecision(request, reason),
            plan,
            PositiveEmissionSourceApi,
            reason);
    }

    public static CompilerHelperRecoveryResult<CompilerVectorTransferEmissionPlan> RecoverFromInstruction(
        HybridCpuOpcode opcode,
        in HybridCpuInstructionWord instruction)
    {
        if (!CompilerVectorTransferPositiveEmissionAbiContract.IsVectorTransferPositiveOpcode(opcode))
        {
            string reason = $"{opcode} is not a VectorTransfer helper/transport opcode.";
            return CompilerHelperRecoveryResult<CompilerVectorTransferEmissionPlan>.NotRecognized(
                CreateRecoveryDecision(
                    sourceValue: false,
                    CompilerHelperRecoveryStatus.NotRecognized,
                    reason),
                RecoverySourceApi,
                reason);
        }

        CompilerVectorTransferEmissionRequest request = RecoverRequest(opcode, in instruction);
        CompilerVectorTransferEmissionPlan plan = CreatePlan(request, instruction);
        string recoveredReason =
            $"{opcode} VectorTransfer helper/transport ABI was recognized; recovery is helper evidence only.";
        return CompilerHelperRecoveryResult<CompilerVectorTransferEmissionPlan>.Recovered(
            plan,
            CreateRecoveryDecision(
                sourceValue: true,
                CompilerHelperRecoveryStatus.HelperAbiRecovered,
                recoveredReason),
            RecoverySourceApi,
            recoveredReason);
    }

    [Obsolete(
        "TryRecoverFromInstruction is a legacy helper/parser bool surface. Use RecoverFromInstruction for typed LegacyApiTranslation and no-authority decision metadata.",
        false)]
    public static bool TryRecoverFromInstruction(
        HybridCpuOpcode opcode,
        in HybridCpuInstructionWord instruction,
        out CompilerVectorTransferEmissionPlan? plan)
    {
        CompilerHelperRecoveryResult<CompilerVectorTransferEmissionPlan> recovery =
            RecoverFromInstruction(opcode, in instruction);
        plan = recovery.Plan;
        return recovery.Status == CompilerHelperRecoveryStatus.HelperAbiRecovered;
    }

    private static CompilerLoweringDecision CreateRecoveryDecision(
        bool sourceValue,
        CompilerHelperRecoveryStatus status,
        string reason)
    {
        return CompilerLoweringDecision.FromLegacyHelperRecoveryBool(
            sourceValue,
            status,
            RecoverySourceApi,
            SemanticIntentKind.VectorStream,
            ExecutionContourKind.StreamEngineVector,
            reason);
    }

    private static CompilerLoweringDecision CreatePositiveEmissionDecision(
        CompilerVectorTransferEmissionRequest request,
        string reason)
    {
        return CompilerLoweringDecision.FromPositiveHelperEmission(
            PositiveEmissionSourceApi,
            SemanticIntentKind.VectorStream,
            ExecutionContourKind.StreamEngineVector,
            $"positive-helper:{request.Mnemonic}:{CompilerVectorTransferPositiveEmissionAbiContract.NoFallbackDecision}",
            reason);
    }

    private static CompilerVectorTransferEmissionPlan CreatePlan(
        CompilerVectorTransferEmissionRequest request,
        in HybridCpuInstructionWord instruction)
    {
        CompilerVectorTransferPositiveEmissionAbiContract.RequireRuntimeHandoffAuthority(request.Mnemonic);

        HybridCpuOpcodeInfo? opcodeInfo = HybridCpuOpcodeCatalog.GetInfo(request.Opcode);
        if (!opcodeInfo.HasValue || opcodeInfo.Value.InstructionClass != HybridCpuInstructionClass.Memory)
        {
            throw new InvalidOperationException(
                $"{request.Mnemonic} compiler emission requires runtime memory opcode authority.");
        }

        if (opcodeInfo.Value.OpCode != (uint)request.Opcode)
        {
            throw new InvalidOperationException(
                $"{request.Mnemonic} compiler emission request does not match the runtime opcode identity.");
        }

        ValidateProjectionMatchesTypedRequest(request, instruction, opcodeInfo.Value);

        return new CompilerVectorTransferEmissionPlan(
            request,
            CompilerVectorTransferPositiveEmissionAbiContract.RuntimeHandoffReference,
            opcodeInfo.Value,
            instruction,
            UsesFallbackPath: false,
            UsesAliasPromotion: false,
            UsesScalarVectorDotOrBackendFallback: false,
            UsesBaseMemoryFallback: false,
            UsesBaseVectorFallback: false,
            UsesScalarHelperFallback: false,
            UsesWideningFmaFallback: false,
            UsesVectorTransposeOrSegmentFallback: false);
    }

    private static void ValidateRequest(CompilerVectorTransferEmissionRequest request)
    {
        request.Shape.Validate(nameof(request.Shape));
        CompilerVectorTransferPositiveEmissionAbiContract.RequireRuntimeHandoffAuthority(request.Mnemonic);
    }

    private static HybridCpuInstructionWord BuildInstruction(CompilerVectorTransferEmissionRequest request)
    {
        CompilerVectorTransferShapeAbi shape = request.Shape;
        ulong destinationPointer = request.Destination.BaseAddress;
        ulong sourcePointer = request.Source.BaseAddress;

        return new HybridCpuInstructionWord
        {
            OpCode = (uint)request.Opcode,
            DataTypeValue = shape.ElementType,
            PredicateMask = shape.PredicateMask,
            Immediate = 0,
            DestSrc1Pointer = request.Kind == CompilerVectorTransferPositiveEmissionKind.Vload
                ? destinationPointer
                : sourcePointer,
            Src2Pointer = request.Kind == CompilerVectorTransferPositiveEmissionKind.Vload
                ? sourcePointer
                : destinationPointer,
            StreamLength = shape.ElementCount,
            Stride = shape.StrideBytes,
            RowStride = 0,
            Indexed = false,
            Is2D = false,
            Reduction = false,
            TailAgnostic = false,
            MaskAgnostic = false,
            VirtualThreadId = 0
        };
    }

    private static CompilerVectorTransferEmissionRequest RecoverRequest(
        HybridCpuOpcode opcode,
        in HybridCpuInstructionWord instruction)
    {
        ValidateRecoveredInstructionShape(opcode, instruction);

        CompilerVectorTransferShapeAbi shape = CompilerVectorTransferShapeAbi.Create(
            instruction.DataTypeValue,
            instruction.StreamLength,
            instruction.Stride,
            instruction.PredicateMask);

        CompilerVectorTransferMemoryAddressAbi destination =
            CompilerVectorTransferMemoryAddressAbi.Create(
                opcode == HybridCpuOpcode.VLOAD
                    ? instruction.DestSrc1Pointer
                    : instruction.Src2Pointer);
        CompilerVectorTransferMemoryAddressAbi source =
            CompilerVectorTransferMemoryAddressAbi.Create(
                opcode == HybridCpuOpcode.VLOAD
                    ? instruction.Src2Pointer
                    : instruction.DestSrc1Pointer);

        return opcode == HybridCpuOpcode.VLOAD
            ? CompilerVectorTransferEmissionRequest.Vload(destination, source, shape)
            : CompilerVectorTransferEmissionRequest.Vstore(source, destination, shape);
    }

    private static void ValidateProjectionMatchesTypedRequest(
        CompilerVectorTransferEmissionRequest request,
        in HybridCpuInstructionWord instruction,
        HybridCpuOpcodeInfo opcodeInfo)
    {
        if (instruction.OpCode != (uint)request.Opcode ||
            instruction.DataTypeValue != request.Shape.ElementType ||
            instruction.PredicateMask != request.Shape.PredicateMask ||
            instruction.StreamLength != request.Shape.ElementCount ||
            instruction.Stride != request.Shape.StrideBytes ||
            instruction.RowStride != 0 ||
            instruction.Indexed ||
            instruction.Is2D ||
            instruction.Reduction ||
            instruction.TailAgnostic ||
            instruction.MaskAgnostic)
        {
            throw new InvalidOperationException(
                $"{request.Mnemonic} compiler emission did not round-trip through the direct vector-transfer helper ABI.");
        }

        switch (request.Kind)
        {
            case CompilerVectorTransferPositiveEmissionKind.Vload:
                if (instruction.DestSrc1Pointer != request.Destination.BaseAddress ||
                    instruction.Src2Pointer != request.Source.BaseAddress)
                {
                    throw new InvalidOperationException(
                        "VLOAD compiler emission lost destination/source address identity.");
                }
                break;
            case CompilerVectorTransferPositiveEmissionKind.Vstore:
                if (instruction.DestSrc1Pointer != request.Source.BaseAddress ||
                    instruction.Src2Pointer != request.Destination.BaseAddress)
                {
                    throw new InvalidOperationException(
                        "VSTORE compiler emission lost source/destination address identity.");
                }
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(request.Kind), request.Kind, "Unknown vector transfer compiler helper kind.");
        }

        if (opcodeInfo.InstructionClass != HybridCpuInstructionClass.Memory)
        {
            throw new InvalidOperationException(
                $"{request.Mnemonic} compiler emission requires memory-class runtime opcode authority.");
        }
    }

    private static void ValidateRecoveredInstructionShape(
        HybridCpuOpcode opcode,
        in HybridCpuInstructionWord instruction)
    {
        if (opcode is not (HybridCpuOpcode.VLOAD or HybridCpuOpcode.VSTORE))
        {
            throw new InvalidOperationException(
                $"Opcode {(ushort)opcode} is not a direct vector transfer helper opcode.");
        }

        if (instruction.StreamLength == 0)
        {
            throw new InvalidOperationException(
                $"Opcode {(ushort)opcode} reached the vector-transfer compiler lowerer with StreamLength == 0. " +
                "The typed helper requires a non-empty transfer shape and must fail closed instead of materializing a hidden no-op contour.");
        }

        if (instruction.Stride == 0)
        {
            throw new InvalidOperationException(
                $"Opcode {(ushort)opcode} reached the vector-transfer compiler lowerer with Stride == 0. " +
                "The typed helper requires an explicit stride and must fail closed instead of reopening the legacy contiguous fallback.");
        }

        if (instruction.Indexed || instruction.Is2D)
        {
            string addressingContour = instruction.Indexed && instruction.Is2D
                ? "indexed+2D"
                : instruction.Indexed
                    ? "indexed"
                    : "2D";

            throw new InvalidOperationException(
                $"Opcode {(ushort)opcode} reached the vector-transfer compiler lowerer with unsupported {addressingContour} addressing. " +
                "The typed helper only publishes the 1D transfer contour and must fail closed instead of reopening a compat surface.");
        }
    }
}
