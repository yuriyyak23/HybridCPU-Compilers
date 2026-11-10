using System;
using System.Collections.Generic;
using HybridCPU.Compiler.Core.IR.Lowering;

namespace HybridCPU.Compiler.Core.IR
{
    /// <summary>
    /// Builds the first normalized IR layer from the current encoded instruction stream.
    /// </summary>
    public sealed partial class HybridCpuIrBuilder
    {
        private const ulong EncodedInstructionSizeBytes = 32;
        private const ulong PackedArchRegisterTupleMask = 0xFFFFFFFFFFFF0000UL;

        private readonly record struct DecodedRegisterTuple(
            IrOperand? First,
            IrOperand? Second,
            IrOperand? Third)
        {
            public bool HasAny => First is not null || Second is not null || Third is not null;
        }

        /// <summary>
        /// Builds an IR program for a single virtual-thread instruction stream.
        /// </summary>
        public IrProgram BuildProgram(
            byte virtualThreadId,
            ReadOnlySpan<HybridCpuInstructionWord> instructions,
            IReadOnlyList<IrLabelDeclaration>? labelDeclarations = null,
            IReadOnlyList<IrEntryPointDeclaration>? entryPointDeclarations = null,
            IReadOnlyList<IrInstructionSourceBinding>? instructionSourceBindings = null,
            IReadOnlyList<IrSectionDeclaration>? sectionDeclarations = null,
            IReadOnlyList<IrFunctionDeclaration>? functionDeclarations = null,
            IrBundleAnnotations? bundleAnnotations = null,
            ulong domainTag = 0,
            IReadOnlyList<IrControlFlowTargetReference>? controlFlowTargetReferences = null)
        {
            List<IrInstruction> irInstructions = new List<IrInstruction>(instructions.Length);

            for (int index = 0; index < instructions.Length; index++)
            {
                IrSlotMetadata slotMetadata = ResolveSlotMetadata(virtualThreadId, index, bundleAnnotations);
                irInstructions.Add(BuildInstruction(index, instructions[index], slotMetadata, domainTag));
            }

            ApplyInstructionSourceBindings(irInstructions, instructionSourceBindings);
            ApplyCanonicalContracts(irInstructions);

            ControlFlowGraph graph = BuildControlFlowGraph(
                irInstructions,
                labelDeclarations,
                entryPointDeclarations,
                controlFlowTargetReferences);
            IReadOnlyList<IrProgramLabel> labels = BuildLabels(virtualThreadId, irInstructions, graph.Blocks, labelDeclarations, entryPointDeclarations);
            IReadOnlyList<IrBasicBlock> blocks = ApplyPrimaryLabels(graph.Blocks, labels);
            IReadOnlyList<IrEntryPointMetadata> entryPoints = BuildEntryPoints(virtualThreadId, irInstructions, blocks, entryPointDeclarations);
            OwnedProgramArtifacts ownedProgram = BuildProgramContainers(virtualThreadId, blocks, labels, entryPoints, sectionDeclarations, functionDeclarations);

            IrProgram program = new IrProgram(
                virtualThreadId,
                irInstructions,
                graph with { Blocks = ownedProgram.Blocks },
                ownedProgram.Labels,
                ownedProgram.EntryPoints,
                ownedProgram.Sections,
                ownedProgram.Functions,
                ownedProgram.Symbols);
            return program with { ValueFlow = HybridCpuNativeValueFlowFactoryV1.Create(program) };
        }

        private static void ApplyInstructionSourceBindings(
            IList<IrInstruction> instructions,
            IReadOnlyList<IrInstructionSourceBinding>? instructionSourceBindings)
        {
            if (instructionSourceBindings is null)
            {
                return;
            }

            for (int index = 0; index < instructionSourceBindings.Count; index++)
            {
                IrInstructionSourceBinding binding = instructionSourceBindings[index];
                ValidateMetadataInstructionIndex(binding.InstructionIndex, instructions.Count, $"instruction[{binding.InstructionIndex}]");
                instructions[binding.InstructionIndex] = instructions[binding.InstructionIndex] with { SourceSpan = binding.SourceSpan };
            }
        }

        private static IrInstruction BuildInstruction(
            int index,
            HybridCpuInstructionWord instruction,
            IrSlotMetadata slotMetadata,
            ulong domainTag)
        {
            HybridCpuOpcode opcode = (HybridCpuOpcode)instruction.OpCode;
            HybridCpuOpcodeInfo? opcodeInfo = HybridCpuOpcodeSemantics.GetOpcodeInfo(opcode);
            ValidateExplicitAcceleratorIntent(opcode, slotMetadata);
            ValidateCanonicalScalarZeroRs2Payload(opcode, instruction);
            CompilerHelperRecoveryResult<CompilerMatrixTileEmissionPlan> matrixTileRecovery =
                CompilerMatrixTileEmissionLowerer.RecoverFromInstruction(
                    opcode,
                    in instruction,
                    slotMetadata.MatrixTileNumericPolicy is MatrixTileNumericPolicy numericPolicy ? numericPolicy : null,
                    slotMetadata.MatrixTileLayoutPolicy is MatrixTileLayoutPolicy layoutPolicy ? layoutPolicy : null);
            CompilerMatrixTileEmissionPlan? matrixTileEmission =
                matrixTileRecovery.Status == CompilerHelperRecoveryStatus.HelperAbiRecovered
                    ? matrixTileRecovery.Plan
                    : null;
            CompilerHelperRecoveryResult<CompilerVectorTransferEmissionPlan> vectorTransferRecovery =
                CompilerVectorTransferEmissionLowerer.RecoverFromInstruction(
                    opcode,
                    in instruction);
            CompilerVectorTransferEmissionPlan? vectorTransferEmission =
                vectorTransferRecovery.Status == CompilerHelperRecoveryStatus.HelperAbiRecovered
                    ? vectorTransferRecovery.Plan
                    : null;
            IReadOnlyList<IrOperand> operands = BuildOperands(opcode, instruction, matrixTileEmission, vectorTransferEmission);
            IReadOnlyList<IrOperand> defs = BuildDefs(opcode, instruction, matrixTileEmission, vectorTransferEmission);
            IReadOnlyList<IrOperand> uses = BuildUses(opcode, instruction, matrixTileEmission, vectorTransferEmission);
            IrOpcodeExecutionProfile executionProfile = HybridCpuHazardModel.GetExecutionProfile(opcode);
            slotMetadata = slotMetadata.WithAdmissionDescriptor(executionProfile);
            IrTypedSlotAdmissionDescriptor admissionDescriptor = slotMetadata.AdmissionDescriptor!.Value;

            IrInstructionAnnotation annotation = new IrInstructionAnnotation(
                ResourceClass: admissionDescriptor.ResourceClass,
                LatencyClass: executionProfile.LatencyClass,
                MinimumLatencyCycles: executionProfile.MinimumLatencyCycles,
                LegalSlots: admissionDescriptor.StructurallyAllowedSlots,
                Serialization: executionProfile.Serialization,
                StructuralResources: executionProfile.StructuralResources,
                ControlFlowKind: ClassifyControlFlow(opcode),
                IsBarrierLike: IsBarrierLike(opcode),
                MayTrap: MayTrap(opcode, opcodeInfo, vectorTransferEmission),
                EncodedBranchTarget: GetEncodedBranchTarget(opcode, instruction),
                ResolvedBranchTargetInstructionIndex: null,
                MemoryReadRegion: BuildMemoryRegion(opcode, instruction, isWrite: false, opcodeInfo, matrixTileEmission, vectorTransferEmission),
                MemoryWriteRegion: BuildMemoryRegion(opcode, instruction, isWrite: true, opcodeInfo, matrixTileEmission, vectorTransferEmission),
                Defs: defs,
                Uses: uses,
                RequiredSlotClass: admissionDescriptor.RequiredSlotClass,
                BindingKind: admissionDescriptor.BindingKind,
                DomainTag: domainTag,
                StealabilityHint: slotMetadata.StealabilityHint);

            HybridCpuOpcodeInfo classifiedInfo = opcodeInfo ?? new HybridCpuOpcodeInfo(
                (uint)opcode,
                opcode.ToString(),
                HybridCpuOpcodeCategory.Scalar,
                0,
                HybridCpuInstructionFlags.None,
                1,
                0,
                HybridCpuInstructionClass.ScalarAlu,
                HybridCpuSerializationClass.Free);
            HybridCpuInstructionClass instructionClass = classifiedInfo.InstructionClass;
            HybridCpuSerializationClass serializationClass = classifiedInfo.SerializationClass;

            return new IrInstruction(
                Index: index,
                VirtualThreadId: slotMetadata.VirtualThreadId,
                EncodedAddress: (ulong)index * EncodedInstructionSizeBytes,
                Opcode: opcode,
                DataType: instruction.DataTypeValue,
                PredicateMask: instruction.PredicateMask,
                Immediate: instruction.Immediate,
                StreamLength: instruction.StreamLength,
                Stride: instruction.Stride,
                RowStride: instruction.RowStride,
                Indexed: instruction.Indexed,
                Is2D: instruction.Is2D,
                Reduction: instruction.Reduction,
                TailAgnostic: instruction.TailAgnostic,
                MaskAgnostic: instruction.MaskAgnostic,
                Operands: operands,
                Annotation: annotation)
            {
                InstructionClass = instructionClass,
                SerializationClass = serializationClass,
                DmaStreamComputeDescriptor = slotMetadata.DmaStreamComputeDescriptor,
                AcceleratorCommandDescriptor = slotMetadata.AcceleratorCommandDescriptor,
                ExternalDescriptorEvidence = slotMetadata.ExternalDescriptorEvidence,
                MatrixTileEmission = matrixTileEmission,
                VectorTransferEmission = vectorTransferEmission,
            };
        }

        private static IrSlotMetadata ResolveSlotMetadata(
            byte defaultVirtualThreadId,
            int instructionIndex,
            IrBundleAnnotations? bundleAnnotations)
        {
            if (bundleAnnotations is not null
                && bundleAnnotations.TryGetInstructionSlotMetadata(instructionIndex, out IrInstructionSlotMetadata metadata))
            {
                return IrSlotMetadata.FromInstructionMetadata(metadata);
            }

            return IrSlotMetadata.DefaultForVirtualThread(defaultVirtualThreadId);
        }

        private static void ValidateExplicitAcceleratorIntent(
            HybridCpuOpcode opcode,
            IrSlotMetadata slotMetadata)
        {
            bool hasDmaStreamDescriptor = slotMetadata.DmaStreamComputeDescriptor is not null;
            bool hasAcceleratorDescriptor = slotMetadata.AcceleratorCommandDescriptor is not null;
            if (hasDmaStreamDescriptor && hasAcceleratorDescriptor)
            {
                throw new InvalidOperationException(
                    "Compiler typed sideband cannot carry lane6 DmaStreamCompute and lane7 L7-SDC descriptors on the same instruction.");
            }

            if (hasDmaStreamDescriptor &&
                !HybridCpuOpcodeSemantics.IsDmaStreamComputeOpcode(opcode))
            {
                throw new InvalidOperationException(
                    "DmaStreamCompute descriptor sideband may only accompany the native lane6 DmaStreamCompute compiler contour.");
            }

            if (!IsSystemDeviceCommandOpcode(opcode))
            {
                if (hasAcceleratorDescriptor)
                {
                    throw new InvalidOperationException(
                        "AcceleratorCommandDescriptor sideband may only accompany explicit L7-SDC compiler accelerator intent.");
                }

                return;
            }

            if (opcode != HybridCpuOpcode.ACCEL_SUBMIT ||
                !hasAcceleratorDescriptor)
            {
                throw new InvalidOperationException(
                    "Compiler L7-SDC emission requires explicit accelerator intent and typed ACCEL_SUBMIT descriptor sideband before native opcode emission.");
            }
        }

        private static bool IsSystemDeviceCommandOpcode(HybridCpuOpcode opcode) => opcode is
            HybridCpuOpcode.ACCEL_CANCEL or
            HybridCpuOpcode.ACCEL_FENCE or
            HybridCpuOpcode.ACCEL_POLL or
            HybridCpuOpcode.ACCEL_QUERY_CAPS or
            HybridCpuOpcode.ACCEL_STATUS or
            HybridCpuOpcode.ACCEL_SUBMIT or
            HybridCpuOpcode.ACCEL_WAIT;

        private static IReadOnlyList<IrOperand> BuildOperands(
            HybridCpuOpcode opcode,
            HybridCpuInstructionWord instruction,
            CompilerMatrixTileEmissionPlan? matrixTileEmission = null,
            CompilerVectorTransferEmissionPlan? vectorTransferEmission = null)
        {
            if (HybridCpuOpcodeSemantics.IsDmaStreamComputeOpcode(opcode))
            {
                return Array.Empty<IrOperand>();
            }

            if (matrixTileEmission is not null)
            {
                return BuildMatrixTileOperands(matrixTileEmission);
            }

            if (vectorTransferEmission is not null)
            {
                return BuildVectorTransferOperands(vectorTransferEmission);
            }

            List<IrOperand> operands = new List<IrOperand>(8);
            DecodedRegisterTuple word1Registers = DecodeWord1Registers(opcode, instruction);
            if (word1Registers.HasAny)
            {
                AddDecodedOperands(operands, word1Registers);
            }
            else
            {
                AddIfNotNull(operands, CreatePointerOperand("destsrc1", instruction.DestSrc1Pointer));
            }

            DecodedRegisterTuple controlRegisters = DecodeControlRegisters(opcode, instruction);
            if (controlRegisters.HasAny)
            {
                AddDecodedOperands(operands, controlRegisters);
            }
            else
            {
                AddIfNotNull(operands, CreatePointerOperand("src2", instruction.Src2Pointer));
            }

            if (instruction.Immediate != 0)
            {
                operands.Add(new IrOperand(IrOperandKind.Immediate, instruction.Immediate, "imm"));
            }

            if (instruction.PredicateMask != 0)
            {
                operands.Add(new IrOperand(IrOperandKind.PredicateMask, instruction.PredicateMask, "pred"));
            }

            if (instruction.StreamLength != 0)
            {
                operands.Add(new IrOperand(IrOperandKind.StreamLength, instruction.StreamLength, "vl"));
            }

            if (instruction.Stride != 0)
            {
                operands.Add(new IrOperand(IrOperandKind.Stride, instruction.Stride, "stride"));
            }

            if (instruction.RowStride != 0)
            {
                operands.Add(new IrOperand(IrOperandKind.RowStride, instruction.RowStride, "rowStride"));
            }

            return operands;
        }

        private static IReadOnlyList<IrOperand> BuildDefs(
            HybridCpuOpcode opcode,
            HybridCpuInstructionWord instruction,
            CompilerMatrixTileEmissionPlan? matrixTileEmission = null,
            CompilerVectorTransferEmissionPlan? vectorTransferEmission = null)
        {
            if (HybridCpuOpcodeSemantics.IsDmaStreamComputeOpcode(opcode))
            {
                return Array.Empty<IrOperand>();
            }

            if (matrixTileEmission is not null)
            {
                return BuildMatrixTileDefs(matrixTileEmission);
            }

            if (vectorTransferEmission is not null)
            {
                return BuildVectorTransferDefs(vectorTransferEmission);
            }

            List<IrOperand> defs = new List<IrOperand>(1);
            DecodedRegisterTuple word1Registers = DecodeWord1Registers(opcode, instruction);
            if (word1Registers.HasAny)
            {
                if (WritesDecodedDestination(opcode))
                {
                    AddIfNotNull(defs, word1Registers.First);
                }

                return defs;
            }

            IrOperand? destOperand = CreatePointerOperand("destsrc1", instruction.DestSrc1Pointer);

            if (destOperand is not null && WritesPrimaryOperand(opcode))
            {
                defs.Add(destOperand);
            }

            return defs;
        }

        private static IReadOnlyList<IrOperand> BuildUses(
            HybridCpuOpcode opcode,
            HybridCpuInstructionWord instruction,
            CompilerMatrixTileEmissionPlan? matrixTileEmission = null,
            CompilerVectorTransferEmissionPlan? vectorTransferEmission = null)
        {
            if (HybridCpuOpcodeSemantics.IsDmaStreamComputeOpcode(opcode))
            {
                return Array.Empty<IrOperand>();
            }

            if (matrixTileEmission is not null)
            {
                return BuildMatrixTileUses(matrixTileEmission);
            }

            if (vectorTransferEmission is not null)
            {
                return BuildVectorTransferUses(vectorTransferEmission);
            }

            List<IrOperand> uses = new List<IrOperand>(6);
            DecodedRegisterTuple word1Registers = DecodeWord1Registers(opcode, instruction);
            DecodedRegisterTuple controlRegisters = DecodeControlRegisters(opcode, instruction);

            if (ClassifyControlFlow(opcode) != IrControlFlowKind.None)
            {
                if (controlRegisters.HasAny)
                {
                    AddDecodedOperands(uses, controlRegisters);
                }
                else if (word1Registers.HasAny)
                {
                    AddIfNotNull(uses, word1Registers.Second);
                    AddIfNotNull(uses, word1Registers.Third);
                }
            }
            else if (word1Registers.HasAny)
            {
                AddIfNotNull(uses, word1Registers.Second);
                AddIfNotNull(uses, word1Registers.Third);
            }

            IrOperand? destOperand = CreatePointerOperand("destsrc1", instruction.DestSrc1Pointer);
            IrOperand? src2Operand = CreatePointerOperand("src2", instruction.Src2Pointer);

            if (destOperand is not null && ReadsPrimaryOperand(opcode) && !word1Registers.HasAny)
            {
                uses.Add(destOperand);
            }

            if (src2Operand is not null && ReadsSecondaryOperand(opcode) && !controlRegisters.HasAny)
            {
                uses.Add(src2Operand);
            }

            if (instruction.Immediate != 0 && ReadsImmediate(opcode))
            {
                uses.Add(new IrOperand(IrOperandKind.Immediate, instruction.Immediate, "imm"));
            }

            if (instruction.PredicateMask != 0)
            {
                uses.Add(new IrOperand(IrOperandKind.PredicateMask, instruction.PredicateMask, "pred"));
            }

            return uses;
        }

        private static IReadOnlyList<IrOperand> BuildMatrixTileOperands(
            CompilerMatrixTileEmissionPlan matrixTileEmission)
        {
            List<IrOperand> operands = new List<IrOperand>(8);
            CompilerMatrixTileEmissionRequest request = matrixTileEmission.Request;
            switch (request.Kind)
            {
                case CompilerMatrixTilePositiveEmissionKind.MtileLoad:
                    operands.Add(CreateMemoryAddressOperand("tileMemoryBase", request.MemoryFaultAbi!.Value.BaseAddress));
                    operands.Add(CreateTileOperand("tileDestination", request.DestinationTile.TileId));
                    break;
                case CompilerMatrixTilePositiveEmissionKind.MtileStore:
                    operands.Add(CreateTileOperand("tileSource", request.PrimaryTile.TileId));
                    operands.Add(CreateMemoryAddressOperand("tileMemoryBase", request.MemoryFaultAbi!.Value.BaseAddress));
                    break;
                case CompilerMatrixTilePositiveEmissionKind.MtileMacc:
                    operands.Add(CreateTileOperand("tileLeftSource", request.PrimaryTile.TileId));
                    operands.Add(CreateTileOperand("tileRightSource", request.SecondaryTile.TileId));
                    operands.Add(CreateTileOperand("tileAccumulator", request.DestinationTile.TileId));
                    break;
                case CompilerMatrixTilePositiveEmissionKind.Mtranspose:
                    operands.Add(CreateTileOperand("tileSource", request.PrimaryTile.TileId));
                    operands.Add(CreateTileOperand("tileDestination", request.DestinationTile.TileId));
                    break;
                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(matrixTileEmission),
                        request.Kind,
                        "Unknown MTILE compiler helper kind.");
            }

            MatrixTileCanonicalDescriptorAbi descriptor =
                request.Descriptor.CanonicalDescriptor;
            operands.Add(new IrOperand(IrOperandKind.StreamLength, matrixTileEmission.EncodedInstruction.StreamLength, "tileElementCount"));
            operands.Add(new IrOperand(IrOperandKind.Immediate, descriptor.Columns, "tileColumns"));
            operands.Add(new IrOperand(IrOperandKind.RowStride, descriptor.StrideBytes, "tileRowStrideBytes"));
            return operands;
        }

        private static IReadOnlyList<IrOperand> BuildMatrixTileDefs(
            CompilerMatrixTileEmissionPlan matrixTileEmission)
        {
            CompilerMatrixTileEmissionRequest request = matrixTileEmission.Request;
            return request.Kind switch
            {
                CompilerMatrixTilePositiveEmissionKind.MtileLoad =>
                    [CreateTileOperand("tileDestination", request.DestinationTile.TileId)],
                CompilerMatrixTilePositiveEmissionKind.MtileStore =>
                    Array.Empty<IrOperand>(),
                CompilerMatrixTilePositiveEmissionKind.MtileMacc =>
                    [CreateTileOperand("tileAccumulator", request.DestinationTile.TileId)],
                CompilerMatrixTilePositiveEmissionKind.Mtranspose =>
                    [CreateTileOperand("tileDestination", request.DestinationTile.TileId)],
                _ => throw new ArgumentOutOfRangeException(
                    nameof(matrixTileEmission),
                    request.Kind,
                    "Unknown MTILE compiler helper kind.")
            };
        }

        private static IReadOnlyList<IrOperand> BuildMatrixTileUses(
            CompilerMatrixTileEmissionPlan matrixTileEmission)
        {
            CompilerMatrixTileEmissionRequest request = matrixTileEmission.Request;
            return request.Kind switch
            {
                CompilerMatrixTilePositiveEmissionKind.MtileLoad =>
                    [CreateMemoryAddressOperand("tileMemoryBase", request.MemoryFaultAbi!.Value.BaseAddress)],
                CompilerMatrixTilePositiveEmissionKind.MtileStore =>
                    [
                        CreateTileOperand("tileSource", request.PrimaryTile.TileId),
                        CreateMemoryAddressOperand("tileMemoryBase", request.MemoryFaultAbi!.Value.BaseAddress)
                    ],
                CompilerMatrixTilePositiveEmissionKind.MtileMacc =>
                    [
                        CreateTileOperand("tileLeftSource", request.PrimaryTile.TileId),
                        CreateTileOperand("tileRightSource", request.SecondaryTile.TileId),
                        CreateTileOperand("tileAccumulator", request.DestinationTile.TileId)
                    ],
                CompilerMatrixTilePositiveEmissionKind.Mtranspose =>
                    [CreateTileOperand("tileSource", request.PrimaryTile.TileId)],
                _ => throw new ArgumentOutOfRangeException(
                    nameof(matrixTileEmission),
                    request.Kind,
                    "Unknown MTILE compiler helper kind.")
            };
        }

        private static IReadOnlyList<IrOperand> BuildVectorTransferOperands(
            CompilerVectorTransferEmissionPlan vectorTransferEmission)
        {
            CompilerVectorTransferEmissionRequest request = vectorTransferEmission.Request;
            List<IrOperand> operands = new List<IrOperand>(5);
            switch (request.Kind)
            {
                case CompilerVectorTransferPositiveEmissionKind.Vload:
                    operands.Add(CreateMemoryAddressOperand("vectorDestinationBase", request.Destination.BaseAddress));
                    operands.Add(CreateMemoryAddressOperand("vectorSourceBase", request.Source.BaseAddress));
                    break;
                case CompilerVectorTransferPositiveEmissionKind.Vstore:
                    operands.Add(CreateMemoryAddressOperand("vectorSourceBase", request.Source.BaseAddress));
                    operands.Add(CreateMemoryAddressOperand("vectorDestinationBase", request.Destination.BaseAddress));
                    break;
                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(vectorTransferEmission),
                        request.Kind,
                        "Unknown vector transfer compiler helper kind.");
            }

            operands.Add(new IrOperand(IrOperandKind.StreamLength, request.Shape.ElementCount, "vectorElementCount"));
            operands.Add(new IrOperand(IrOperandKind.Stride, request.Shape.StrideBytes, "vectorStrideBytes"));
            if (request.Shape.PredicateMask != 0)
            {
                operands.Add(new IrOperand(IrOperandKind.PredicateMask, request.Shape.PredicateMask, "vectorPredicateMask"));
            }

            return operands;
        }

        private static IReadOnlyList<IrOperand> BuildVectorTransferDefs(
            CompilerVectorTransferEmissionPlan vectorTransferEmission)
        {
            CompilerVectorTransferEmissionRequest request = vectorTransferEmission.Request;
            return
            [
                CreateMemoryAddressOperand("vectorDestinationBase", request.Destination.BaseAddress)
            ];
        }

        private static IReadOnlyList<IrOperand> BuildVectorTransferUses(
            CompilerVectorTransferEmissionPlan vectorTransferEmission)
        {
            CompilerVectorTransferEmissionRequest request = vectorTransferEmission.Request;
            return
            [
                CreateMemoryAddressOperand("vectorSourceBase", request.Source.BaseAddress)
            ];
        }

        private static IrOperand CreateTileOperand(string name, ushort tileId) =>
            new(IrOperandKind.Tile, tileId, name);

        private static IrOperand CreateMemoryAddressOperand(string name, ulong address) =>
            new(IrOperandKind.MemoryAddress, address, name);

        private static bool HasFallthroughEdge(IrControlFlowKind controlFlowKind)
        {
            return controlFlowKind == IrControlFlowKind.None || controlFlowKind == IrControlFlowKind.ConditionalBranch;
        }

        private static bool IsBlockTerminator(IrInstruction instruction)
        {
            return instruction.Annotation.ControlFlowKind != IrControlFlowKind.None || instruction.Annotation.IsBarrierLike;
        }

        private static IrOperand? CreatePointerOperand(string name, ulong value)
        {
            return value == 0 ? null : new IrOperand(IrOperandKind.Pointer, value, name);
        }

        private static IrOperand? CreateArchitecturalRegisterOperand(string name, byte registerId)
        {
            return registerId == HybridCpuInstructionWord.NoArchReg
                ? null
                : new IrOperand(IrOperandKind.ArchitecturalRegister, registerId, name);
        }

        private static void AddIfNotNull(ICollection<IrOperand> operands, IrOperand? operand)
        {
            if (operand is not null)
            {
                operands.Add(operand);
            }
        }

        private static void AddDecodedOperands(ICollection<IrOperand> operands, DecodedRegisterTuple decodedRegisters)
        {
            AddIfNotNull(operands, decodedRegisters.First);
            AddIfNotNull(operands, decodedRegisters.Second);
            AddIfNotNull(operands, decodedRegisters.Third);
        }

        private static DecodedRegisterTuple DecodeWord1Registers(HybridCpuOpcode opcode, HybridCpuInstructionWord instruction)
        {
            bool usesPackedWord1 =
                UsesCanonicalRdOnlyCounterPayload(opcode) ||
                UsesPackedArchRegisterWord1(opcode);

            if (HybridCpuOpcodeSemantics.TryResolveRetainedCompatibilityScalarMemoryDirection(opcode, out _) ||
                !usesPackedWord1 ||
                (!UsesCanonicalRdOnlyCounterPayload(opcode) && !HasPackedArchRegisterTuple(instruction.Word1)) ||
                !HybridCpuInstructionWord.TryUnpackArchRegs(instruction.Word1, out byte rd, out byte rs1, out byte rs2))
            {
                return default;
            }

            return new DecodedRegisterTuple(
                CreateArchitecturalRegisterOperand("rd", rd),
                UsesCanonicalRdOnlyCounterPayload(opcode)
                    ? null
                    : CreateArchitecturalRegisterOperand("rs1", rs1),
                UsesCanonicalRdOnlyCounterPayload(opcode) ||
                UsesCanonicalScalarZeroRs2(opcode)
                    ? null
                    : CreateArchitecturalRegisterOperand("rs2", rs2));
        }

        private static bool UsesPackedArchRegisterWord1(HybridCpuOpcode opcode)
        {
            HybridCpuOpcodeInfo? info = HybridCpuOpcodeSemantics.GetOpcodeInfo(opcode);
            bool isVectorConfigSystemSingleton = opcode is
                HybridCpuOpcode.VSETVL or
                HybridCpuOpcode.VSETVLI or
                HybridCpuOpcode.VSETIVLI;

            return info is not null &&
                (!info.Value.IsVector || isVectorConfigSystemSingleton) &&
                info.Value.InstructionClass is
                HybridCpuInstructionClass.ScalarAlu or
                HybridCpuInstructionClass.Memory or
                HybridCpuInstructionClass.ControlFlow or
                HybridCpuInstructionClass.Atomic or
                HybridCpuInstructionClass.System or
                HybridCpuInstructionClass.Csr or
                HybridCpuInstructionClass.SmtVt or
                HybridCpuInstructionClass.Vmx;
        }

        private static DecodedRegisterTuple DecodeControlRegisters(HybridCpuOpcode opcode, HybridCpuInstructionWord instruction)
        {
            if (ClassifyControlFlow(opcode) == IrControlFlowKind.None ||
                !HasPackedArchRegisterTuple(instruction.Src2Pointer) ||
                !HybridCpuInstructionWord.TryUnpackArchRegs(instruction.Src2Pointer, out byte first, out byte second, out byte third))
            {
                return default;
            }

            return new DecodedRegisterTuple(
                CreateArchitecturalRegisterOperand("ctrl0", first),
                CreateArchitecturalRegisterOperand("ctrl1", second),
                CreateArchitecturalRegisterOperand("ctrl2", third));
        }

        private static bool HasPackedArchRegisterTuple(ulong packedRegisters)
        {
            return (packedRegisters & PackedArchRegisterTupleMask) != 0;
        }

        private static void ValidateCanonicalScalarZeroRs2Payload(
            HybridCpuOpcode opcode,
            HybridCpuInstructionWord instruction)
        {
            if (UsesCanonicalRdOnlyCounterPayload(opcode))
            {
                if (!HybridCpuInstructionWord.TryUnpackArchRegs(
                        instruction.Word1,
                        out _,
                        out byte counterRs1,
                        out byte counterRs2) ||
                    counterRs1 != 0 ||
                    counterRs2 != 0 ||
                    instruction.Immediate != 0)
                {
                    throw new InvalidOperationException(
                        $"{opcode} compiler emission requires canonical counter Word1=(rd, x0, x0) and Immediate=0.");
                }

                return;
            }

            if (!UsesCanonicalScalarZeroRs2(opcode))
            {
                return;
            }

            if (!HybridCpuInstructionWord.TryUnpackArchRegs(
                    instruction.Word1,
                    out _,
                    out _,
                    out byte scalarRs2) ||
                scalarRs2 != 0)
            {
                throw new InvalidOperationException(
                    $"{opcode} compiler emission requires canonical scalar Word1=(rd, rs1, x0).");
            }

            if (UsesCanonicalScalarUnaryZeroRs2(opcode) &&
                instruction.Immediate != 0)
            {
                throw new InvalidOperationException(
                    $"{opcode} compiler emission requires canonical scalar-unary Immediate=0.");
            }
        }

        private static bool UsesCanonicalScalarZeroRs2(HybridCpuOpcode opcode)
        {
            return UsesCanonicalScalarImmediateZeroRs2(opcode) ||
                   UsesCanonicalScalarUnaryZeroRs2(opcode);
        }

        private static bool UsesCanonicalRdOnlyCounterPayload(HybridCpuOpcode opcode) =>
            opcode is HybridCpuOpcode.RDCYCLE;

        private static bool UsesCanonicalScalarImmediateZeroRs2(HybridCpuOpcode opcode) =>
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
                HybridCpuOpcode.SLLI_UW;

        private static bool UsesCanonicalScalarUnaryZeroRs2(HybridCpuOpcode opcode) =>
            opcode is
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

        private static IrResourceClass ClassifyResource(HybridCpuOpcode opcode)
        {
            if (HybridCpuOpcodeSemantics.IsDmaStreamComputeOpcode(opcode))
            {
                return IrResourceClass.DmaStream;
            }

            if (HybridCpuOpcodeSemantics.IsLoadStoreOpcode(opcode))
            {
                return IrResourceClass.LoadStore;
            }

            if (ClassifyControlFlow(opcode) != IrControlFlowKind.None)
            {
                return IrResourceClass.ControlFlow;
            }

            if (IsSystemInstruction(opcode))
            {
                return IrResourceClass.System;
            }

            return IsVectorInstruction(opcode) ? IrResourceClass.VectorAlu : IrResourceClass.ScalarAlu;
        }

        private static IrLatencyClass ClassifyLatency(HybridCpuOpcode opcode)
        {
            if (HybridCpuOpcodeSemantics.IsLoadStoreOpcode(opcode))
            {
                return IrLatencyClass.LoadUse;
            }

            if (ClassifyControlFlow(opcode) != IrControlFlowKind.None)
            {
                return IrLatencyClass.ControlFlow;
            }

            if (IsBarrierLike(opcode) || IsSystemInstruction(opcode))
            {
                return IrLatencyClass.Serialized;
            }

            return IsVectorInstruction(opcode) ? IrLatencyClass.Vector : IrLatencyClass.SingleCycle;
        }

        private static IrControlFlowKind ClassifyControlFlow(HybridCpuOpcode opcode)
        {
            HybridCpuOpcode semanticOpcode = HybridCpuOpcodeSemantics.NormalizeSemanticOpcode(opcode);
            HybridCpuOpcodeInfo? opcodeInfo = HybridCpuOpcodeSemantics.GetOpcodeInfo(opcode);
            if (opcodeInfo.HasValue)
            {
                if (opcodeInfo.Value.InstructionClass == HybridCpuInstructionClass.ControlFlow)
                {
                    if (TryResolvePublishedControlFlowKind(semanticOpcode, out IrControlFlowKind controlFlowKind))
                    {
                        return controlFlowKind;
                    }

                    return opcodeInfo.Value.OperandCount == 1
                        ? IrControlFlowKind.UnconditionalBranch
                        : IrControlFlowKind.ConditionalBranch;
                }

                if (opcodeInfo.Value.InstructionClass == HybridCpuInstructionClass.System)
                {
                    if (TryResolvePublishedSystemEventKind(opcode, out SystemEventKind systemEventKind))
                    {
                        return systemEventKind is
                            SystemEventKind.Wfi or
                            SystemEventKind.Ebreak
                            ? IrControlFlowKind.Stop
                            : IrControlFlowKind.None;
                    }

                    return IrControlFlowKind.None;
                }
            }

            return HybridCpuOpcodeSemantics.TryResolveRetainedCompatibilityControlFlowKind(
                opcode,
                out IrControlFlowKind retainedControlFlowKind)
                ? retainedControlFlowKind
                : IrControlFlowKind.None;
        }

        private static bool TryResolvePublishedControlFlowKind(
            HybridCpuOpcode opcode,
            out IrControlFlowKind controlFlowKind)
        {
            controlFlowKind = IrControlFlowKind.None;

            HybridCpuOpcodeInfo? opcodeInfo = HybridCpuOpcodeSemantics.GetOpcodeInfo(opcode);
            if (!opcodeInfo.HasValue || opcodeInfo.Value.InstructionClass != HybridCpuInstructionClass.ControlFlow)
            {
                return false;
            }

            controlFlowKind = opcode switch
            {
                HybridCpuOpcode.JAL => IrControlFlowKind.UnconditionalBranch,
                HybridCpuOpcode.JALR => IrControlFlowKind.Return,
                HybridCpuOpcode.BEQ or
                HybridCpuOpcode.BNE or
                HybridCpuOpcode.BLT or
                HybridCpuOpcode.BGE or
                HybridCpuOpcode.BLTU or
                HybridCpuOpcode.BGEU => IrControlFlowKind.ConditionalBranch,
                _ => IrControlFlowKind.None,
            };

            return controlFlowKind != IrControlFlowKind.None;
        }

        private static bool TryResolvePublishedSystemEventKind(
            HybridCpuOpcode opcode,
            out SystemEventKind systemEventKind)
        {
            systemEventKind = default;

            HybridCpuOpcodeInfo? opcodeInfo = HybridCpuOpcodeSemantics.GetOpcodeInfo(opcode);
            if (!opcodeInfo.HasValue ||
                opcodeInfo.Value.InstructionClass is not (HybridCpuInstructionClass.System or HybridCpuInstructionClass.SmtVt))
            {
                return false;
            }

            switch (opcode)
            {
                case HybridCpuOpcode.FENCE:
                    systemEventKind = SystemEventKind.Fence;
                    return true;
                case HybridCpuOpcode.FENCE_I:
                    systemEventKind = SystemEventKind.FenceI;
                    return true;
                case HybridCpuOpcode.ECALL:
                    systemEventKind = SystemEventKind.Ecall;
                    return true;
                case HybridCpuOpcode.EBREAK:
                    systemEventKind = SystemEventKind.Ebreak;
                    return true;
                case HybridCpuOpcode.MRET:
                    systemEventKind = SystemEventKind.Mret;
                    return true;
                case HybridCpuOpcode.SRET:
                    systemEventKind = SystemEventKind.Sret;
                    return true;
                case HybridCpuOpcode.WFI:
                    systemEventKind = SystemEventKind.Wfi;
                    return true;
                case HybridCpuOpcode.WFE:
                    systemEventKind = SystemEventKind.Wfe;
                    return true;
                case HybridCpuOpcode.SEV:
                    systemEventKind = SystemEventKind.Sev;
                    return true;
                case HybridCpuOpcode.YIELD:
                    systemEventKind = SystemEventKind.Yield;
                    return true;
                case HybridCpuOpcode.POD_BARRIER:
                    systemEventKind = SystemEventKind.PodBarrier;
                    return true;
                case HybridCpuOpcode.VT_BARRIER:
                    systemEventKind = SystemEventKind.VtBarrier;
                    return true;
                default:
                    return false;
            }
        }

        private static bool ReadsPrimaryOperand(HybridCpuOpcode opcode)
        {
            if (HybridCpuOpcodeSemantics.TryResolveRetainedCompatibilityVectorTransferDirection(
                    opcode,
                    out bool isVectorWriteContour) &&
                !isVectorWriteContour)
            {
                return false;
            }

            if (HybridCpuOpcodeSemantics.TryResolveRetainedCompatibilityScalarMemoryDirection(
                    opcode,
                    out bool isWriteContour) &&
                !isWriteContour)
            {
                return false;
            }

            return opcode switch
            {
                _ when ClassifyControlFlow(opcode) != IrControlFlowKind.None => false,
                _ when IsSystemInstruction(opcode) => false,
                _ => true
            };
        }

        private static bool WritesPrimaryOperand(HybridCpuOpcode opcode)
        {
            if (HybridCpuOpcodeSemantics.TryResolveRetainedCompatibilityVectorTransferDirection(
                    opcode,
                    out bool isVectorWriteContour) &&
                isVectorWriteContour)
            {
                return false;
            }

            if (HybridCpuOpcodeSemantics.TryResolveRetainedCompatibilityScalarMemoryDirection(
                    opcode,
                    out bool isWriteContour) &&
                isWriteContour)
            {
                return false;
            }

            return opcode switch
            {
                _ when ClassifyControlFlow(opcode) != IrControlFlowKind.None => false,
                _ when IsSystemInstruction(opcode) && opcode != HybridCpuOpcode.CSRRS => false,
                _ => true
            };
        }

        private static bool ReadsSecondaryOperand(HybridCpuOpcode opcode)
        {
            return opcode switch
            {
                HybridCpuOpcode.CSRRS => false,
                _ when opcode == HybridCpuOpcode.VNOT => false,
                _ when ClassifyControlFlow(opcode) != IrControlFlowKind.None => false,
                _ => true
            };
        }

        private static bool ReadsImmediate(HybridCpuOpcode opcode)
        {
            return opcode switch
            {
                _ when HasEncodedBranchTarget(opcode) => true,
                _ => false
            };
        }

        private static bool HasEncodedBranchTarget(HybridCpuOpcode opcode)
        {
            return ClassifyControlFlow(opcode) is IrControlFlowKind.ConditionalBranch or IrControlFlowKind.UnconditionalBranch;
        }

        private static ulong? GetEncodedBranchTarget(HybridCpuOpcode opcode, HybridCpuInstructionWord instruction)
        {
            if (!HasEncodedBranchTarget(opcode))
            {
                return null;
            }

            if (instruction.Immediate != 0)
            {
                return instruction.Immediate;
            }

            if (instruction.Src2Pointer != 0 && !HasPackedArchRegisterTuple(instruction.Src2Pointer))
            {
                return instruction.Src2Pointer;
            }

            return null;
        }

        private static bool IsBarrierLike(HybridCpuOpcode opcode)
        {
            return HybridCpuOpcodeSemantics.IsBarrierLike(opcode);
        }

        private static bool MayTrap(
            HybridCpuOpcode opcode,
            HybridCpuOpcodeInfo? opcodeInfo,
            CompilerVectorTransferEmissionPlan? vectorTransferEmission = null)
        {
            HybridCpuOpcodeInfo? resolvedOpcodeInfo = opcodeInfo ?? HybridCpuOpcodeSemantics.GetOpcodeInfo(opcode);
            if (CompilerMatrixTilePositiveEmissionAbiContract.IsMatrixTilePositiveOpcode(opcode))
            {
                return opcode is HybridCpuOpcode.MTILE_LOAD or HybridCpuOpcode.MTILE_STORE;
            }

            if (vectorTransferEmission is not null ||
                CompilerVectorTransferPositiveEmissionAbiContract.IsVectorTransferPositiveOpcode(opcode))
            {
                return true;
            }

            if (HybridCpuOpcodeSemantics.IsLoadStoreOpcode(opcode, resolvedOpcodeInfo))
            {
                return true;
            }

            if (resolvedOpcodeInfo.HasValue &&
                resolvedOpcodeInfo.Value.InstructionClass == HybridCpuInstructionClass.ControlFlow)
            {
                return ClassifyControlFlow(opcode) == IrControlFlowKind.UnconditionalBranch;
            }

            if (resolvedOpcodeInfo.HasValue &&
                resolvedOpcodeInfo.Value.InstructionClass == HybridCpuInstructionClass.ScalarAlu)
            {
                return HybridCpuOpcodeSemantics.IsSignedDivideTrapContour(opcode, resolvedOpcodeInfo);
            }

            return opcode switch
            {
                _ when IsSystemInstruction(opcode, resolvedOpcodeInfo) => true,
                _ => false
            };
        }

        private static IrMemoryRegion? BuildMemoryRegion(
            HybridCpuOpcode opcode,
            HybridCpuInstructionWord instruction,
            bool isWrite,
            HybridCpuOpcodeInfo? opcodeInfo,
            CompilerMatrixTileEmissionPlan? matrixTileEmission = null,
            CompilerVectorTransferEmissionPlan? vectorTransferEmission = null)
        {
            if (matrixTileEmission is not null)
            {
                return BuildMatrixTileMemoryRegion(matrixTileEmission, isWrite);
            }

            if (vectorTransferEmission is not null)
            {
                return BuildVectorTransferMemoryRegion(vectorTransferEmission, isWrite);
            }

            bool hasRequiredDirection = isWrite
                ? HybridCpuOpcodeSemantics.UsesLoadStoreWritePath(opcode, opcodeInfo)
                : HybridCpuOpcodeSemantics.UsesLoadStoreReadPath(opcode, opcodeInfo);

            if (!hasRequiredDirection)
            {
                return null;
            }

            ulong? address = instruction.Src2Pointer != 0
                ? instruction.Src2Pointer
                : instruction.DestSrc1Pointer;

            if (address is null)
            {
                return null;
            }

            uint length = EstimateMemoryLength(instruction);
            return new IrMemoryRegion(address.Value, length, isWrite);
        }

        private static uint EstimateMemoryLength(HybridCpuInstructionWord instruction)
        {
            ulong logicalLength = instruction.StreamLength == 0 ? 1UL : instruction.StreamLength;
            ulong bytes = logicalLength * (ulong)HybridCpuDataTypes.SizeOf(instruction.DataTypeValue);
            return bytes > uint.MaxValue ? uint.MaxValue : (uint)bytes;
        }

        private static IrMemoryRegion? BuildMatrixTileMemoryRegion(
            CompilerMatrixTileEmissionPlan matrixTileEmission,
            bool isWrite)
        {
            CompilerMatrixTileEmissionRequest request = matrixTileEmission.Request;
            bool isLoad = request.Kind == CompilerMatrixTilePositiveEmissionKind.MtileLoad;
            bool isStore = request.Kind == CompilerMatrixTilePositiveEmissionKind.MtileStore;
            if ((!isWrite && !isLoad) ||
                (isWrite && !isStore))
            {
                return null;
            }

            MatrixTileMemoryShapeValidationResult validation =
                matrixTileEmission.MemoryValidation
                ?? throw new InvalidOperationException(
                    $"{request.Mnemonic} compiler memory region requires runtime tile memory/fault validation.");
            ulong length = validation.TotalByteFootprint == 0
                ? 1UL
                : validation.TotalByteFootprint;
            return new IrMemoryRegion(
                request.MemoryFaultAbi!.Value.BaseAddress,
                length > uint.MaxValue ? uint.MaxValue : (uint)length,
                isWrite);
        }

        private static IrMemoryRegion? BuildVectorTransferMemoryRegion(
            CompilerVectorTransferEmissionPlan vectorTransferEmission,
            bool isWrite)
        {
            CompilerVectorTransferEmissionRequest request = vectorTransferEmission.Request;
            ulong address = isWrite
                ? request.Destination.BaseAddress
                : request.Source.BaseAddress;

            ulong length = request.Shape.EffectiveByteLength;
            return new IrMemoryRegion(
                address,
                length > uint.MaxValue ? uint.MaxValue : (uint)length,
                isWrite);
        }

        private static bool IsVectorInstruction(HybridCpuOpcode opcode)
        {
            return HybridCpuOpcodeSemantics.IsVectorInstruction(opcode);
        }

        private static bool IsSystemInstruction(HybridCpuOpcode opcode, HybridCpuOpcodeInfo? opcodeInfo = null)
        {
            return HybridCpuOpcodeSemantics.IsSystemInstruction(opcode, opcodeInfo);
        }

        private static bool WritesDecodedDestination(HybridCpuOpcode opcode)
        {
            if (ClassifyControlFlow(opcode) != IrControlFlowKind.None)
            {
                HybridCpuOpcode semanticOpcode = HybridCpuOpcodeSemantics.NormalizeSemanticOpcode(opcode);
                return semanticOpcode is HybridCpuOpcode.JAL or HybridCpuOpcode.JALR;
            }

            HybridCpuOpcodeInfo? opcodeInfo = HybridCpuOpcodeSemantics.GetOpcodeInfo(opcode);
            if (opcodeInfo.HasValue)
            {
                if (opcodeInfo.Value.InstructionClass == HybridCpuInstructionClass.Memory &&
                    opcodeInfo.Value.SerializationClass == HybridCpuSerializationClass.MemoryOrdered)
                {
                    return false;
                }

                if (opcodeInfo.Value.InstructionClass == HybridCpuInstructionClass.Csr &&
                    opcodeInfo.Value.OperandCount == 0)
                {
                    return false;
                }
            }

            if (HybridCpuOpcodeSemantics.TryResolveRetainedCompatibilityScalarMemoryDirection(
                    opcode,
                    out bool isRetainedWriteContour))
            {
                return !isRetainedWriteContour;
            }

            if (HybridCpuOpcodeSemantics.TryResolveRetainedCompatibilityVectorTransferDirection(
                    opcode,
                    out bool isRetainedVectorWriteContour))
            {
                return !isRetainedVectorWriteContour;
            }

            return true;
        }
    }
}
