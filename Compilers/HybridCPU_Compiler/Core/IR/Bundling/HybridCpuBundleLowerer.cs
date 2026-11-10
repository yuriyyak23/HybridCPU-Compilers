using System;
using System.Collections.Generic;
using System.Threading;

namespace HybridCPU.Compiler.Core.IR
{
    /// <summary>
    /// Lowers materialized Stage 6 bundles into backend-facing `HybridCpuInstructionBundle` structures.
    /// </summary>
    public sealed class HybridCpuBundleLowerer
    {
        /// <summary>
        /// Lowers one bundled IR program into backend `HybridCpuInstructionBundle` instances in program order.
        /// </summary>
        public IReadOnlyList<HybridCpuInstructionBundle> LowerProgram(IrProgramBundlingResult programBundlingResult)
            => LowerProgram(CancellationToken.None, programBundlingResult);

        public IReadOnlyList<HybridCpuInstructionBundle> LowerProgram(
            CancellationToken cancellationToken, IrProgramBundlingResult programBundlingResult)
        {
            ArgumentNullException.ThrowIfNull(programBundlingResult);

            if (programBundlingResult.ProgramSchedule.Program.Instructions.Any(instruction => instruction.Annotation.FixedFrameSlotIdentity is not null))
                throw new InvalidOperationException("Unresolved fixed-frame operands remain in the bundled program.");

            cancellationToken.ThrowIfCancellationRequested();
            List<HybridCpuInstructionBundle> loweredBundles = new List<HybridCpuInstructionBundle>();
            foreach (IrBasicBlockBundlingResult blockResult in programBundlingResult.BlockResults)
            {
                cancellationToken.ThrowIfCancellationRequested();
                loweredBundles.AddRange(LowerBlock(cancellationToken, blockResult));
            }

            cancellationToken.ThrowIfCancellationRequested();
            return loweredBundles;
        }

        /// <summary>
        /// Emits per-physical-bundle sideband annotations aligned with lowered bundle slots.
        /// </summary>
        public IReadOnlyList<IrBundleAnnotations> EmitAnnotationsForProgram(
            IrProgramBundlingResult programBundlingResult)
            => EmitAnnotationsForProgram(CancellationToken.None, programBundlingResult);

        public IReadOnlyList<IrBundleAnnotations> EmitAnnotationsForProgram(
            CancellationToken cancellationToken, IrProgramBundlingResult programBundlingResult)
        {
            ArgumentNullException.ThrowIfNull(programBundlingResult);

            cancellationToken.ThrowIfCancellationRequested();
            List<IrBundleAnnotations> annotations = new List<IrBundleAnnotations>();
            foreach (IrBasicBlockBundlingResult blockResult in programBundlingResult.BlockResults)
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (IrMaterializedBundle bundle in blockResult.Bundles)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    annotations.Add(EmitAnnotationsForBundle(bundle));
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            return annotations;
        }

        /// <summary>
        /// Lowers one bundled IR basic block into backend `HybridCpuInstructionBundle` instances in cycle order.
        /// </summary>
        public IReadOnlyList<HybridCpuInstructionBundle> LowerBlock(IrBasicBlockBundlingResult blockBundlingResult)
            => LowerBlock(CancellationToken.None, blockBundlingResult);

        public IReadOnlyList<HybridCpuInstructionBundle> LowerBlock(
            CancellationToken cancellationToken, IrBasicBlockBundlingResult blockBundlingResult)
        {
            ArgumentNullException.ThrowIfNull(blockBundlingResult);

            cancellationToken.ThrowIfCancellationRequested();
            List<HybridCpuInstructionBundle> loweredBundles = new List<HybridCpuInstructionBundle>(blockBundlingResult.Bundles.Count);
            foreach (IrMaterializedBundle bundle in blockBundlingResult.Bundles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                loweredBundles.Add(LowerBundle(bundle));
            }

            cancellationToken.ThrowIfCancellationRequested();
            return loweredBundles;
        }

        /// <summary>
        /// Lowers one materialized Stage 6 bundle into a backend `HybridCpuInstructionBundle`.
        /// </summary>
        public HybridCpuInstructionBundle LowerBundle(IrMaterializedBundle bundle)
        {
            ArgumentNullException.ThrowIfNull(bundle);

            HybridCpuInstructionBundle loweredBundle = new HybridCpuInstructionBundle();

            foreach (IrMaterializedBundleSlot slot in bundle.Slots)
            {
                HybridCpuInstructionWord loweredInstruction = slot.Instruction is null
                    ? CreateNopInstruction()
                    : LowerInstruction(slot.Instruction);

                loweredBundle.SetInstruction(slot.SlotIndex, loweredInstruction);
            }

            return loweredBundle;
        }

        /// <summary>
        /// Emits typed-slot facts for a single materialized bundle as a side-channel.
        /// The caller pairs the facts with the lowered <see cref="HybridCpuInstructionBundle"/>.
        /// </summary>
        public static IrTypedSlotBundleFacts EmitFactsForBundle(IrMaterializedBundle bundle)
        {
            ArgumentNullException.ThrowIfNull(bundle);
            return HybridCpuTypedSlotFactsEmitter.EmitFacts(bundle);
        }

        public static IrBundleAnnotations EmitAnnotationsForBundle(IrMaterializedBundle bundle)
        {
            ArgumentNullException.ThrowIfNull(bundle);

            IrInstructionSlotMetadata[] slotMetadata = new IrInstructionSlotMetadata[HybridCpuInstructionBundle.SlotCount];
            for (int slotIndex = 0; slotIndex < slotMetadata.Length; slotIndex++)
            {
                slotMetadata[slotIndex] = IrInstructionSlotMetadata.Default;
            }

            foreach (IrMaterializedBundleSlot slot in bundle.Slots)
            {
                if (slot.Instruction is not { } instruction)
                {
                    continue;
                }

                slotMetadata[slot.SlotIndex] = BuildInstructionSlotMetadata(instruction);
            }

            return new IrBundleAnnotations(slotMetadata);
        }

        private static HybridCpuInstructionWord LowerInstruction(IrInstruction instruction)
        {
            if (instruction.Annotation.FixedFrameSlotIdentity is not null)
                throw new InvalidOperationException("A symbolic fixed-frame access must be resolved by register/frame allocation before encoding.");
            if (instruction.Annotation.ControlFlowKind == IrControlFlowKind.Call)
            {
                bool finalRegisters = !instruction.Annotation.Uses.Any(static operand => operand.Kind == IrOperandKind.VirtualValue) &&
                    !instruction.Annotation.Defs.Any(static operand => operand.Kind == IrOperandKind.VirtualValue);
                bool link = instruction.Operands.Any(static operand =>
                    operand.Kind == IrOperandKind.ArchitecturalRegister && operand.Name == "rd" &&
                    operand.Value == HybridCPU.Compiler.Core.Target.HybridCpuNativeAbiContractV2.ReturnAddressRegister);
                bool direct = instruction.Opcode == HybridCpuOpcode.JAL && link;
                bool longDirect = instruction.Opcode == HybridCpuOpcode.JALR && link && instruction.Immediate == 0 &&
                    instruction.Annotation.BranchTargetSymbolName is not null && instruction.Annotation.Uses.Any(static operand =>
                        operand.Kind == IrOperandKind.ArchitecturalRegister && operand.Value == 5);
                bool indirect = instruction.Opcode == HybridCpuOpcode.JALR && link && instruction.Immediate == 0 &&
                    instruction.Annotation.BranchTargetSymbolName is null && instruction.Annotation.Uses.Any(static operand =>
                        operand.Kind == IrOperandKind.ArchitecturalRegister && operand.Value == 5);
                if (!finalRegisters || !(direct || longDirect || indirect))
                    throw new InvalidOperationException(
                        "Managed calls require final ABI/clobber/frame lowering and direct JAL, symbol-backed AUIPC/x5 JALR, or generic x5 JALR transfer.");
            }
            if (instruction.MatrixTileEmission is { } matrixTileEmission)
            {
                HybridCpuInstructionWord matrixTileInstruction = matrixTileEmission.EncodedInstruction;
                matrixTileInstruction.VirtualThreadId = instruction.VirtualThreadId;
                return matrixTileInstruction;
            }

            if (instruction.VectorTransferEmission is { } vectorTransferEmission)
            {
                HybridCpuInstructionWord vectorTransferInstruction = vectorTransferEmission.EncodedInstruction;
                vectorTransferInstruction.VirtualThreadId = instruction.VirtualThreadId;
                return vectorTransferInstruction;
            }

            // Preserve only the surviving legacy-compatible VT hint in word3.
            // Stealability policy no longer round-trips through the encoded VLIW payload.
            HybridCpuInstructionWord loweredInstruction = new HybridCpuInstructionWord
            {
                OpCode = (uint)instruction.Opcode,
                DataTypeValue = instruction.DataType,
                PredicateMask = instruction.PredicateMask,
                Immediate = instruction.Immediate,
                DestSrc1Pointer = GetDestSrc1Pointer(instruction),
                Src2Pointer = GetSrc2Pointer(instruction),
                StreamLength = instruction.StreamLength,
                Stride = instruction.Stride,
                RowStride = instruction.RowStride,
                VirtualThreadId = instruction.Opcode == HybridCpuOpcode.DmaStreamCompute ||
                                  IsSystemDeviceCommandOpcode(instruction.Opcode)
                    ? (byte)0
                    : instruction.VirtualThreadId,
                Indexed = instruction.Indexed,
                Is2D = instruction.Is2D,
                Reduction = instruction.Reduction,
                TailAgnostic = instruction.TailAgnostic,
                MaskAgnostic = instruction.MaskAgnostic
            };

            return loweredInstruction;
        }

        private static IrInstructionSlotMetadata BuildInstructionSlotMetadata(
            IrInstruction instruction)
        {
            IrSlotBindingKind effectiveBindingKind =
                instruction.Annotation.RequiredSlotClass == IrSlotClass.DmaStreamClass
                    ? IrSlotBindingKind.HardPinned
                    : instruction.Annotation.BindingKind;
            IrInstructionSlotMetadata metadata = new IrInstructionSlotMetadata(
                instruction.VirtualThreadId,
                instruction.Annotation.RequiredSlotClass,
                effectiveBindingKind,
                ResolvePinnedLaneId(
                    instruction.Annotation.RequiredSlotClass,
                    effectiveBindingKind == IrSlotBindingKind.HardPinned),
                instruction.Annotation.DomainTag,
                instruction.Annotation.StealabilityHint)
            {
                DmaStreamComputeDescriptor = instruction.DmaStreamComputeDescriptor,
                AcceleratorCommandDescriptor = instruction.AcceleratorCommandDescriptor,
                ExternalDescriptorEvidence = instruction.ExternalDescriptorEvidence,
                MatrixTileNumericPolicy = instruction.MatrixTileEmission?.MatrixTileNumericPolicy,
                MatrixTileLayoutPolicy = instruction.MatrixTileEmission?.MatrixTileLayoutPolicy
            };

            return metadata;
        }

        private static byte ResolvePinnedLaneId(
            IrSlotClass requiredSlotClass,
            bool hardPinned)
        {
            if (!hardPinned)
            {
                return 0;
            }

            return requiredSlotClass switch
            {
                IrSlotClass.BranchControl => 7,
                IrSlotClass.SystemSingleton => 7,
                _ => 0
            };
        }

        private static bool IsSystemDeviceCommandOpcode(HybridCpuOpcode opcode) => opcode is
            HybridCpuOpcode.ACCEL_CANCEL or
            HybridCpuOpcode.ACCEL_FENCE or
            HybridCpuOpcode.ACCEL_POLL or
            HybridCpuOpcode.ACCEL_QUERY_CAPS or
            HybridCpuOpcode.ACCEL_STATUS or
            HybridCpuOpcode.ACCEL_SUBMIT or
            HybridCpuOpcode.ACCEL_WAIT;

        private static ulong GetDestSrc1Pointer(IrInstruction instruction)
        {
            return TryPackArchitecturalRegisterTuple(instruction, "rd", "rs1", "rs2", out ulong packedRegisters)
                ? packedRegisters
                : GetPointerOperand(instruction, "destsrc1");
        }

        private static ulong GetSrc2Pointer(IrInstruction instruction)
        {
            return TryPackArchitecturalRegisterTuple(instruction, "ctrl0", "ctrl1", "ctrl2", out ulong packedRegisters)
                ? packedRegisters
                : GetPointerOperand(instruction, "src2");
        }

        private static HybridCpuInstructionWord CreateNopInstruction()
        {
            return new HybridCpuInstructionWord
            {
                OpCode = (uint)HybridCpuOpcode.Nope,
                DataTypeValue = HybridCpuDataType.INT8,
                StreamLength = 0
            };
        }

        private static ulong GetPointerOperand(IrInstruction instruction, string operandName)
        {
            foreach (IrOperand operand in instruction.Operands)
            {
                if (operand.Kind == IrOperandKind.Pointer && string.Equals(operand.Name, operandName, StringComparison.Ordinal))
                {
                    return operand.Value;
                }
            }

            return 0;
        }

        private static bool TryPackArchitecturalRegisterTuple(
            IrInstruction instruction,
            string firstName,
            string secondName,
            string thirdName,
            out ulong packedRegisters)
        {
            byte firstRegister = HybridCpuInstructionWord.NoArchReg;
            bool usesCanonicalRdOnlyCounterPayload = UsesCanonicalRdOnlyCounterPayload(
                instruction.Opcode,
                firstName,
                secondName,
                thirdName);
            byte secondRegister = usesCanonicalRdOnlyCounterPayload
                ? (byte)0
                : HybridCpuInstructionWord.NoArchReg;
            byte thirdRegister = usesCanonicalRdOnlyCounterPayload ||
                                 UsesCanonicalScalarZeroRs2(instruction.Opcode, firstName, secondName, thirdName)
                ? (byte)0
                : HybridCpuInstructionWord.NoArchReg;
            bool hasAnyRegister = false;

            foreach (IrOperand operand in instruction.Operands)
            {
                if (operand.Kind is not (IrOperandKind.ArchitecturalRegister or IrOperandKind.Pointer) ||
                    operand.Value > byte.MaxValue)
                {
                    continue;
                }

                if (string.Equals(operand.Name, firstName, StringComparison.Ordinal))
                {
                    firstRegister = (byte)operand.Value;
                    hasAnyRegister = true;
                }
                else if (string.Equals(operand.Name, secondName, StringComparison.Ordinal))
                {
                    secondRegister = (byte)operand.Value;
                    hasAnyRegister = true;
                }
                else if (string.Equals(operand.Name, thirdName, StringComparison.Ordinal))
                {
                    thirdRegister = (byte)operand.Value;
                    hasAnyRegister = true;
                }
            }

            if (!hasAnyRegister)
            {
                packedRegisters = 0;
                return false;
            }

            packedRegisters = HybridCpuInstructionWord.PackArchRegs(
                firstRegister,
                secondRegister,
                thirdRegister);
            return true;
        }

        private static bool UsesCanonicalScalarZeroRs2(
            HybridCpuOpcode opcode,
            string firstName,
            string secondName,
            string thirdName)
        {
            return string.Equals(firstName, "rd", StringComparison.Ordinal) &&
                   string.Equals(secondName, "rs1", StringComparison.Ordinal) &&
                   string.Equals(thirdName, "rs2", StringComparison.Ordinal) &&
                   opcode is
                       HybridCpuOpcode.ADDIW or
                       HybridCpuOpcode.SLLIW or
                       HybridCpuOpcode.SRLIW or
                       HybridCpuOpcode.SRAIW or
                       HybridCpuOpcode.ROLI or
                       HybridCpuOpcode.RORI or
                       HybridCpuOpcode.BSETI or
                       HybridCpuOpcode.BCLRI or
                       HybridCpuOpcode.BINVI or
                       HybridCpuOpcode.BEXTI or
                       HybridCpuOpcode.SLLI_UW or
                       HybridCpuOpcode.SEXT_W or
                       HybridCpuOpcode.ZEXT_W or
                       HybridCpuOpcode.CLZ or
                       HybridCpuOpcode.CTZ or
                       HybridCpuOpcode.CPOP or
                       HybridCpuOpcode.SEXT_B or
                       HybridCpuOpcode.SEXT_H or
                       HybridCpuOpcode.ZEXT_H or
                       HybridCpuOpcode.REV8 or
                       HybridCpuOpcode.BREV8;
        }

        private static bool UsesCanonicalRdOnlyCounterPayload(
            HybridCpuOpcode opcode,
            string firstName,
            string secondName,
            string thirdName)
        {
            return string.Equals(firstName, "rd", StringComparison.Ordinal) &&
                   string.Equals(secondName, "rs1", StringComparison.Ordinal) &&
                   string.Equals(thirdName, "rs2", StringComparison.Ordinal) &&
                   opcode is HybridCpuOpcode.RDCYCLE;
        }
    }
}
