using HybridCPU.Compiler.Core.IR;
using HybridCPU_ISE.Arch;
using YAKSys_Hybrid_CPU;
using YAKSys_Hybrid_CPU.Arch;
using YAKSys_Hybrid_CPU.Core;
using YAKSys_Hybrid_CPU.Core.Execution.DmaStreamCompute;
using YAKSys_Hybrid_CPU.Core.Execution.ExternalAccelerators.Auth;
using YAKSys_Hybrid_CPU.Core.Execution.ExternalAccelerators.Descriptors;
using YAKSys_Hybrid_CPU.Core.Registers;
using static YAKSys_Hybrid_CPU.Processor.CPU_Core;
using RuntimeFrontendMode = YAKSys_Hybrid_CPU.FrontendMode;

namespace HybridCPU.Compiler.Core.Threading
{
    public enum DmaStreamComputeCompilerAdoptionMode : byte
    {
        Compatibility = 0,
        Strict = 1,
        Future = 2
    }

    /// <summary>
    /// Thread-specific compilation context for 4-way SMT.
    /// Each HybridCpuThreadCompilerContext represents one virtual thread (0-3) within a physical core
    /// and compiles through <see cref="HybridCpuCanonicalCompiler"/> directly.
    /// </summary>
    public partial class HybridCpuThreadCompilerContext
    {
        private readonly VtId _virtualThreadId;
        private RuntimeFrontendMode _frontendMode = RuntimeFrontendMode.NativeVLIW;
        private ulong _domainTag;

        private const int MAX_INSTRUCTIONS_PER_THREAD = 1024;
        private readonly VLIW_Instruction[] _instructions = new VLIW_Instruction[MAX_INSTRUCTIONS_PER_THREAD];
        private readonly InstructionSlotMetadata[] _instructionSlotMetadata = new InstructionSlotMetadata[MAX_INSTRUCTIONS_PER_THREAD];
        private readonly IrExternalOperationIntent?[] _externalOperationIntents = new IrExternalOperationIntent?[MAX_INSTRUCTIONS_PER_THREAD];
        private readonly IrExternalOperationDescriptorIdentity?[] _externalOperationDescriptorIdentities = new IrExternalOperationDescriptorIdentity?[MAX_INSTRUCTIONS_PER_THREAD];
        private int _instructionCount = 0;

        /// <summary>
        /// Creates a new thread compiler context for a specific virtual thread.
        /// </summary>
        /// <param name="virtualThreadId">VT ID (0-3) for 4-way SMT</param>
        public HybridCpuThreadCompilerContext(byte virtualThreadId)
        {
            _virtualThreadId = VtId.Create(virtualThreadId);
            _domainTag = 0;
        }

        /// <summary>
        /// Virtual Thread ID (0-3) assigned to this context.
        /// </summary>
        public byte VirtualThreadId => _virtualThreadId.Value;

        /// <summary>
        /// Typed virtual thread identity used by the phase-02 register model.
        /// </summary>
        public VtId TypedVirtualThreadId => _virtualThreadId;

        /// <summary>
        /// Frontend profile selected for canonical compilation of this virtual thread.
        /// </summary>
        public RuntimeFrontendMode FrontendMode
        {
            get => _frontendMode;
            set
            {
                if (_frontendMode == value)
                {
                    return;
                }

                _frontendMode = value;
                InvalidateCanonicalCompileCache();
            }
        }

        /// <summary>
        /// Security domain tag for this VT (enforced by MEM_DOMAIN_CERT CSR at runtime).
        /// </summary>
        public ulong DomainTag
        {
            get => _domainTag;
            set
            {
                if (_domainTag == value)
                {
                    return;
                }

                _domainTag = value;
                RefreshInstructionDomainTags(value);
                InvalidateCanonicalCompileCache();
            }
        }

        /// <summary>
        /// Number of instructions compiled for this VT so far.
        /// </summary>
        public int InstructionCount => _instructionCount;

        /// <summary>
        /// Compiles a VLIW instruction with automatic VT sideband metadata.
        /// This is the primary API for thread-aware compilation.
        /// </summary>
        public void CompileInstruction(
            uint opCode, byte dataType, byte predicate, ushort immediate,
            ulong destSrc1, ulong src2, ulong streamLength, ushort stride,
            StealabilityPolicy stealabilityPolicy)
        {
            ValidateNoDirectSystemDeviceCommandEmission(opCode);
            ValidateNoDirectMatrixTileEmission(opCode);
            ValidateNoDirectVectorTransferEmission(opCode);
            uint encodedStreamLength = RequireVliwUInt32(streamLength, nameof(streamLength));
            if (_instructionCount >= MAX_INSTRUCTIONS_PER_THREAD)
                throw new InvalidOperationException($"VT-{_virtualThreadId.Value}: Instruction buffer overflow (max {MAX_INSTRUCTIONS_PER_THREAD})");

            VLIW_Instruction inst = new VLIW_Instruction
            {
                OpCode = opCode,
                DataType = dataType,
                PredicateMask = predicate,
                Immediate = immediate,
                DestSrc1Pointer = destSrc1,
                Src2Pointer = src2,
                StreamLength = encodedStreamLength,
                Stride = stride
            };

            _instructions[_instructionCount] = inst;
            _instructionSlotMetadata[_instructionCount] = new InstructionSlotMetadata(
                _virtualThreadId,
                BuildSlotMetadata(opCode, stealabilityPolicy, _domainTag));
            _instructionCount++;
            InvalidateCanonicalCompileCache();
        }

        public CompilerAcceleratorLoweringDecision CompileAcceleratorSubmit(
            IrAcceleratorIntent intent,
            CompilerAcceleratorCapabilityModel capabilityModel,
            StealabilityPolicy stealabilityPolicy = StealabilityPolicy.NotStealable)
        {
            ArgumentNullException.ThrowIfNull(intent);
            ArgumentNullException.ThrowIfNull(capabilityModel);
            EnsureAcceleratorIntentDescriptorAdmissible(intent);
            CompilerAcceleratorLoweringDecision decision =
                capabilityModel.Decide(intent);

            if (decision.Mode == AcceleratorLoweringMode.Reject)
            {
                throw new InvalidOperationException(decision.Reason);
            }

            if (!decision.EmitsAcceleratorSubmit)
            {
                return decision;
            }

            IrAcceleratorCommand command = decision.Command
                ?? throw new InvalidOperationException(
                    "ACCEL_SUBMIT lowering decision did not carry an accelerator command.");
            CompileAcceleratorSubmit(
                command,
                stealabilityPolicy);
            return decision;
        }

        public CompilerAcceleratorLoweringDecision CompileAcceleratorSubmit(
            AcceleratorCommandDescriptor descriptor,
            CompilerAcceleratorCapabilityModel capabilityModel,
            byte tokenDestinationRegister = 1,
            AcceleratorLoweringMode requestedMode = AcceleratorLoweringMode.EmitAcceleratorSubmit,
            StealabilityPolicy stealabilityPolicy = StealabilityPolicy.NotStealable)
        {
            ArgumentNullException.ThrowIfNull(descriptor);
            return CompileAcceleratorSubmit(
                IrAcceleratorIntent.ForMatMul(
                    descriptor,
                    tokenDestinationRegister,
                    requestedMode),
                capabilityModel,
                stealabilityPolicy);
        }

        private void CompileAcceleratorSubmit(
            IrAcceleratorCommand command,
            StealabilityPolicy stealabilityPolicy)
        {
            ArgumentNullException.ThrowIfNull(command);
            if (command.AllowRuntimeFallbackAfterSubmit)
            {
                throw new InvalidOperationException(
                    "ACCEL_SUBMIT compiler emission cannot carry a runtime fallback promise.");
            }

            EnsureAcceleratorCommandDescriptorAdmissible(
                command.DescriptorSideband.CommandDescriptor);
            EnsureInstructionCapacity();

            const uint opCode = (uint)InstructionsEnum.ACCEL_SUBMIT;
            VLIW_Instruction inst = new VLIW_Instruction
            {
                OpCode = opCode,
                DataTypeValue = DataTypeEnum.INT32,
                PredicateMask = 0,
                Immediate = 0,
                Word1 = VLIW_Instruction.PackArchRegs(
                    command.TokenDestinationRegister,
                    VLIW_Instruction.NoArchReg,
                    VLIW_Instruction.NoArchReg),
                Src2Pointer = 0,
                StreamLength = 0,
                Stride = 0,
                VirtualThreadId = 0
            };

            _instructions[_instructionCount] = inst;
            _instructionSlotMetadata[_instructionCount] = new InstructionSlotMetadata(
                _virtualThreadId,
                BuildSlotMetadata(opCode, stealabilityPolicy, _domainTag))
            {
                AcceleratorCommandDescriptor = command.DescriptorSideband.CommandDescriptor
            };
            _externalOperationIntents[_instructionCount] = command.ExternalOperation;
            _externalOperationDescriptorIdentities[_instructionCount] =
                IrExternalOperationDescriptorIdentity.Require(
                    command.DescriptorSideband.DescriptorReference.DescriptorIdentityHash,
                    nameof(command));
            _instructionCount++;
            InvalidateCanonicalCompileCache();
        }

        public DmaStreamComputeDescriptor CompileDmaStreamComputeDescriptor(
            ReadOnlySpan<byte> descriptorBytes,
            DmaStreamComputeOwnerGuardDecision ownerGuardDecision,
            DmaStreamComputeDescriptorReference? descriptorReference = null,
            DmaStreamComputeCompilerAdoptionMode mode = DmaStreamComputeCompilerAdoptionMode.Strict,
            StealabilityPolicy stealabilityPolicy = StealabilityPolicy.NotStealable)
        {
            ValidateDmaStreamComputeMode(mode);

            DmaStreamComputeValidationResult validation =
                DmaStreamComputeDescriptorParser.Parse(
                    descriptorBytes,
                    ownerGuardDecision,
                    descriptorReference);
            if (!validation.IsDescriptorAbiAccepted)
            {
                throw new InvalidOperationException(
                    $"DmaStreamCompute compiler emission rejected descriptor: {validation.Fault}. {validation.Message}");
            }

            DmaStreamComputeDescriptor descriptor =
                validation.RequireDescriptorForAdmission();
            CompileDmaStreamCompute(
                descriptor,
                mode,
                stealabilityPolicy);
            return descriptor;
        }

        public void CompileDmaStreamCompute(
            DmaStreamComputeDescriptor descriptor,
            DmaStreamComputeCompilerAdoptionMode mode = DmaStreamComputeCompilerAdoptionMode.Strict,
            StealabilityPolicy stealabilityPolicy = StealabilityPolicy.NotStealable)
        {
            ValidateDmaStreamComputeMode(mode);
            EnsureDmaStreamComputeDescriptorAdmissible(descriptor);
            EnsureInstructionCapacity();

            const uint opCode = (uint)InstructionsEnum.DmaStreamCompute;
            VLIW_Instruction inst = new VLIW_Instruction
            {
                OpCode = opCode,
                DataType = 0,
                PredicateMask = 0,
                Immediate = 0,
                DestSrc1Pointer = 0,
                Src2Pointer = 0,
                StreamLength = 0,
                Stride = 0,
                VirtualThreadId = 0
            };

            _instructions[_instructionCount] = inst;
            _instructionSlotMetadata[_instructionCount] = new InstructionSlotMetadata(
                _virtualThreadId,
                BuildSlotMetadata(opCode, stealabilityPolicy, _domainTag))
            {
                DmaStreamComputeDescriptor = descriptor
            };
            _instructionCount++;
            InvalidateCanonicalCompileCache();
        }

        /// <summary>
        /// Retrieves the compiled instruction buffer for this VT.
        /// Returns a span to avoid allocation (HLS-compatible).
        /// </summary>
        public ReadOnlySpan<VLIW_Instruction> GetCompiledInstructions()
        {
            return new ReadOnlySpan<VLIW_Instruction>(_instructions, 0, _instructionCount);
        }

        /// <summary>
        /// Retrieves the canonical bundle annotations accumulated for the compiled instructions.
        /// </summary>
        public VliwBundleAnnotations GetBundleAnnotations()
        {
            if (_instructionCount == 0)
            {
                return VliwBundleAnnotations.Empty;
            }

            InstructionSlotMetadata[] slotMetadata = new InstructionSlotMetadata[_instructionCount];
            Array.Copy(_instructionSlotMetadata, slotMetadata, _instructionCount);
            return new VliwBundleAnnotations(slotMetadata);
        }

        internal IReadOnlyList<IrExternalOperationIntent?> GetExternalOperationIntents()
        {
            IrExternalOperationIntent?[] intents = new IrExternalOperationIntent?[_instructionCount];
            Array.Copy(_externalOperationIntents, intents, _instructionCount);
            return Array.AsReadOnly(intents);
        }

        internal IReadOnlyList<IrExternalOperationLoweringMetadata?> GetExternalOperationLoweringMetadata()
        {
            IrExternalOperationLoweringMetadata?[] metadata = new IrExternalOperationLoweringMetadata?[_instructionCount];
            for (int index = 0; index < metadata.Length; index++)
            {
                if (_externalOperationIntents[index] is not { } intent ||
                    _externalOperationDescriptorIdentities[index] is not { } descriptorIdentity)
                {
                    continue;
                }

                metadata[index] = new IrExternalOperationLoweringMetadata
                {
                    SemanticContractVersion = IrExternalOperationSemanticContract.Version,
                    SourceInstructionIndex = index,
                    DescriptorIdentity = descriptorIdentity,
                    Intent = intent
                };
            }

            return Array.AsReadOnly(metadata);
        }

        private static SlotMetadata BuildSlotMetadata(
            uint opCode,
            StealabilityPolicy stealabilityPolicy,
            ulong domainTag)
        {
            MicroOpAdmissionMetadata admissionMetadata =
                BuildAdmissionMetadata(opCode, stealabilityPolicy, domainTag);

            return new SlotMetadata
            {
                StealabilityPolicy = stealabilityPolicy,
                AdmissionMetadata = admissionMetadata
            };
        }

        private static void ValidateNoDirectSystemDeviceCommandEmission(uint opCode)
        {
            if (OpcodeRegistry.IsSystemDeviceCommandOpcode(opCode))
            {
                throw new InvalidOperationException(
                    "L7-SDC system-device opcodes require explicit accelerator intent; use CompileAcceleratorSubmit with typed descriptor sideband.");
            }
        }

        private static void ValidateNoDirectVectorTransferEmission(uint opCode)
        {
            if (opCode is (uint)InstructionsEnum.VLOAD or (uint)InstructionsEnum.VSTORE)
            {
                throw new InvalidOperationException(
                    "VLOAD/VSTORE require the explicit typed vector load/store helper ABI; raw CompileInstruction transport is not emission authority.");
            }
        }

        private static uint RequireVliwUInt32(ulong value, string parameterName)
        {
            if (value > uint.MaxValue)
            {
                throw new ArgumentOutOfRangeException(
                    parameterName,
                    value,
                    "VLIW carrier field is 32 bits; silent truncation is forbidden.");
            }

            return (uint)value;
        }

        private static MicroOpAdmissionMetadata BuildAdmissionMetadata(
            uint opCode,
            StealabilityPolicy stealabilityPolicy,
            ulong domainTag)
        {
            IrOpcodeExecutionProfile executionProfile =
                HybridCpuHazardModel.GetExecutionProfile((HybridCpuOpcode)opCode);
            SlotPinningKind runtimePinning = executionProfile.DerivedBindingKind == IrSlotBindingKind.HardPinned
                ? SlotPinningKind.HardPinned
                : SlotPinningKind.ClassFlexible;
            SlotClass runtimeSlotClass = (SlotClass)(byte)executionProfile.DerivedSlotClass;
            SlotPlacementMetadata placement = new()
            {
                RequiredSlotClass = runtimeSlotClass,
                PinningKind = runtimePinning,
                PinnedLaneId = ResolvePinnedLaneId(runtimeSlotClass, runtimePinning),
                DomainTag = domainTag
            };

            // Thread-ingress metadata only publishes the placement/domain subset that is
            // already known from the opcode profile plus thread context. Register and
            // memory facts remain deferred to IR/decode truth surfaces.
            return MicroOpAdmissionMetadata.Default with
            {
                IsStealable = stealabilityPolicy == StealabilityPolicy.Stealable,
                DomainTag = domainTag,
                Placement = placement
            };
        }

        private static byte ResolvePinnedLaneId(
            SlotClass requiredSlotClass,
            SlotPinningKind pinningKind)
        {
            if (pinningKind != SlotPinningKind.HardPinned)
            {
                return 0;
            }

            return requiredSlotClass switch
            {
                SlotClass.BranchControl => 7,
                SlotClass.SystemSingleton => 7,
                _ => 0
            };
        }

        private void RefreshInstructionDomainTags(ulong domainTag)
        {
            for (int instructionIndex = 0; instructionIndex < _instructionCount; instructionIndex++)
            {
                InstructionSlotMetadata slotMetadata = _instructionSlotMetadata[instructionIndex];
                SlotMetadata coreSlotMetadata = slotMetadata.SlotMetadata ?? SlotMetadata.Default;
                MicroOpAdmissionMetadata refreshedAdmission = coreSlotMetadata.AdmissionMetadata with
                {
                    DomainTag = domainTag,
                    Placement = coreSlotMetadata.AdmissionMetadata.Placement with { DomainTag = domainTag }
                };

                _instructionSlotMetadata[instructionIndex] = slotMetadata with
                {
                    SlotMetadata = coreSlotMetadata with { AdmissionMetadata = refreshedAdmission }
                };
            }
        }

        private static void ValidateDmaStreamComputeMode(
            DmaStreamComputeCompilerAdoptionMode mode)
        {
            if (mode is not
                (DmaStreamComputeCompilerAdoptionMode.Compatibility or
                 DmaStreamComputeCompilerAdoptionMode.Strict or
                 DmaStreamComputeCompilerAdoptionMode.Future))
            {
                throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown DmaStreamCompute compiler adoption mode.");
            }
        }

        private static void EnsureDmaStreamComputeDescriptorAdmissible(
            DmaStreamComputeDescriptor descriptor)
        {
            ArgumentNullException.ThrowIfNull(descriptor);

            if (descriptor.AbiVersion != DmaStreamComputeDescriptorParser.CurrentAbiVersion ||
                descriptor.HeaderSize != DmaStreamComputeDescriptorParser.CurrentHeaderSize ||
                descriptor.TotalSize < DmaStreamComputeDescriptorParser.CurrentHeaderSize)
            {
                throw new InvalidOperationException(
                    "DmaStreamCompute compiler emission requires a supported descriptor ABI/header before native opcode emission.");
            }

            if (descriptor.DescriptorReference.DescriptorSize != 0 &&
                descriptor.DescriptorReference.DescriptorSize < descriptor.TotalSize)
            {
                throw new InvalidOperationException(
                    "DmaStreamCompute compiler emission requires descriptor reference sideband to cover the accepted payload.");
            }

            if (descriptor.DescriptorReference.DescriptorIdentityHash != 0 &&
                descriptor.DescriptorReference.DescriptorIdentityHash != descriptor.DescriptorIdentityHash)
            {
                throw new InvalidOperationException(
                    "DmaStreamCompute compiler emission requires descriptor reference identity to match the accepted payload.");
            }

            CompilerRuntimeGuardObservation ownerGuardObservation =
                CompilerRuntimeGuardObservation.FromDmaStreamComputeOwnerGuard(
                    descriptor.OwnerGuardDecision,
                    "HybridCpuThreadCompilerContext.EnsureDmaStreamComputeDescriptorAdmissible",
                    "DSC owner guard is a runtime-owned admission observation only; it grants no compiler runtime legality, execution, publication, commit or retire authority.");
            if (!ownerGuardObservation.ObservedGuardAllowsProgress)
            {
                throw new InvalidOperationException(
                    "DmaStreamCompute compiler emission requires an accepted owner/domain guard decision before descriptor emission.");
            }

            if (descriptor.OwnerGuardDecision.DescriptorOwnerBinding is null ||
                !descriptor.OwnerGuardDecision.DescriptorOwnerBinding.Equals(descriptor.OwnerBinding))
            {
                throw new InvalidOperationException(
                    "DmaStreamCompute compiler emission requires the guard decision to match descriptor owner binding.");
            }

            if (descriptor.OwnerBinding.DeviceId != DmaStreamComputeDescriptor.CanonicalLane6DeviceId)
            {
                throw new InvalidOperationException(
                    "DmaStreamCompute compiler emission requires the canonical lane6 DMA/stream device id.");
            }

            if (!IsSupportedDmaStreamComputeOperation(descriptor.Operation) ||
                !IsSupportedDmaStreamComputeElementType(descriptor.ElementType) ||
                !IsSupportedDmaStreamComputeShape(descriptor.Operation, descriptor.Shape) ||
                descriptor.RangeEncoding != DmaStreamComputeRangeEncoding.InlineContiguous ||
                descriptor.PartialCompletionPolicy != DmaStreamComputePartialCompletionPolicy.AllOrNone)
            {
                throw new InvalidOperationException(
                    "DmaStreamCompute compiler emission rejected unsupported descriptor semantics before native opcode emission.");
            }

            if (!HasNonEmptyRanges(descriptor.ReadMemoryRanges) ||
                !HasNonEmptyRanges(descriptor.NormalizedReadMemoryRanges) ||
                !HasNonEmptyRanges(descriptor.WriteMemoryRanges) ||
                !HasNonEmptyRanges(descriptor.NormalizedWriteMemoryRanges) ||
                descriptor.NormalizedFootprintHash == 0)
            {
                throw new InvalidOperationException(
                    "DmaStreamCompute compiler emission requires validated read/write footprint sideband before native opcode emission.");
            }
        }

        private static void EnsureAcceleratorCommandDescriptorAdmissible(
            AcceleratorCommandDescriptor descriptor)
        {
            ArgumentNullException.ThrowIfNull(descriptor);

            if (descriptor.AbiVersion != AcceleratorDescriptorParser.CurrentAbiVersion ||
                descriptor.HeaderSize != AcceleratorDescriptorParser.CurrentHeaderSize ||
                descriptor.DescriptorSize < AcceleratorDescriptorParser.CurrentHeaderSize)
            {
                throw new InvalidOperationException(
                    "L7-SDC compiler emission requires a supported descriptor ABI/header before native opcode emission.");
            }

            if (descriptor.DescriptorReference.DescriptorSize != 0 &&
                descriptor.DescriptorReference.DescriptorSize < descriptor.DescriptorSize)
            {
                throw new InvalidOperationException(
                    "L7-SDC compiler emission requires descriptor reference sideband to cover the accepted payload.");
            }

            if (descriptor.DescriptorReference.DescriptorIdentityHash != 0 &&
                descriptor.DescriptorReference.DescriptorIdentityHash != descriptor.Identity.DescriptorIdentityHash)
            {
                throw new InvalidOperationException(
                    "L7-SDC compiler emission requires descriptor reference identity to match the accepted payload.");
            }

            bool guardBacked =
                AcceleratorOwnerDomainGuard.Default.IsDescriptorGuardBacked(
                    descriptor,
                    out string guardBackedMessage);
            CompilerRuntimeGuardObservation ownerGuardObservation =
                CompilerRuntimeGuardObservation.FromAcceleratorDescriptorOwnerGuard(
                    descriptor.OwnerGuardDecision,
                    "HybridCpuThreadCompilerContext.EnsureAcceleratorCommandDescriptorAdmissible.OwnerGuard",
                    "L7-SDC descriptor owner guard is a runtime-owned admission observation only; it grants no compiler runtime legality, execution, publication, commit or retire authority.");
            if (!ownerGuardObservation.ObservedGuardAllowsProgress ||
                !guardBacked)
            {
                throw new InvalidOperationException(
                    "L7-SDC compiler emission requires guard-backed owner/domain acceptance before ACCEL_SUBMIT emission. " +
                    guardBackedMessage);
            }

            AcceleratorGuardDecision submitGuard =
                AcceleratorOwnerDomainGuard.Default.EnsureBeforeSubmit(
                    descriptor,
                    descriptor.OwnerGuardDecision.Evidence);
            CompilerRuntimeGuardObservation submitGuardObservation =
                CompilerRuntimeGuardObservation.FromAcceleratorSubmitGuard(
                    submitGuard,
                    "HybridCpuThreadCompilerContext.EnsureAcceleratorCommandDescriptorAdmissible.SubmitGuard",
                    "L7-SDC submit guard is a runtime-owned admission observation only; it grants no compiler runtime legality, execution, publication, commit or retire authority.");
            if (!submitGuardObservation.ObservedGuardAllowsProgress)
            {
                throw new InvalidOperationException(
                    "L7-SDC compiler emission requires submit guard acceptance before native opcode emission. " +
                    submitGuard.Message);
            }

            if (descriptor.OwnerGuardDecision.DescriptorOwnerBinding is null ||
                !descriptor.OwnerGuardDecision.DescriptorOwnerBinding.Equals(descriptor.OwnerBinding))
            {
                throw new InvalidOperationException(
                    "L7-SDC compiler emission requires the guard decision to match descriptor owner binding.");
            }

            if (descriptor.PartialCompletionPolicy != AcceleratorPartialCompletionPolicy.AllOrNone ||
                descriptor.NormalizedFootprint.Hash == 0 ||
                !HasNonEmptyAcceleratorRanges(descriptor.SourceRanges) ||
                !HasNonEmptyAcceleratorRanges(descriptor.DestinationRanges) ||
                !HasNonEmptyAcceleratorRanges(descriptor.NormalizedFootprint.SourceRanges) ||
                !HasNonEmptyAcceleratorRanges(descriptor.NormalizedFootprint.DestinationRanges))
            {
                throw new InvalidOperationException(
                    "L7-SDC compiler emission requires validated AllOrNone source/destination footprint sideband before native opcode emission.");
            }
        }

        private static void EnsureAcceleratorIntentDescriptorAdmissible(
            IrAcceleratorIntent intent)
        {
            if (intent.DescriptorSideband?.CommandDescriptor is not AcceleratorCommandDescriptor descriptor)
            {
                throw new InvalidOperationException(
                    "L7-SDC compiler lowering requires descriptor sideband before capability selection.");
            }

            EnsureAcceleratorCommandDescriptorAdmissible(descriptor);
        }

        private static bool IsSupportedDmaStreamComputeOperation(
            DmaStreamComputeOperationKind operation) =>
            operation is
                DmaStreamComputeOperationKind.Copy or
                DmaStreamComputeOperationKind.Add or
                DmaStreamComputeOperationKind.Mul or
                DmaStreamComputeOperationKind.Fma or
                DmaStreamComputeOperationKind.Reduce;

        private static bool IsSupportedDmaStreamComputeElementType(
            DmaStreamComputeElementType elementType) =>
            elementType is
                DmaStreamComputeElementType.UInt8 or
                DmaStreamComputeElementType.UInt16 or
                DmaStreamComputeElementType.UInt32 or
                DmaStreamComputeElementType.UInt64 or
                DmaStreamComputeElementType.Float32 or
                DmaStreamComputeElementType.Float64;

        private static bool IsSupportedDmaStreamComputeShape(
            DmaStreamComputeOperationKind operation,
            DmaStreamComputeShapeKind shape) =>
            shape == DmaStreamComputeShapeKind.Contiguous1D ||
            (operation == DmaStreamComputeOperationKind.Reduce &&
             shape == DmaStreamComputeShapeKind.FixedReduce);

        private static bool HasNonEmptyRanges(
            System.Collections.Generic.IReadOnlyList<DmaStreamComputeMemoryRange>? ranges)
        {
            if (ranges is null || ranges.Count == 0)
            {
                return false;
            }

            for (int index = 0; index < ranges.Count; index++)
            {
                DmaStreamComputeMemoryRange range = ranges[index];
                if (range.Length == 0 || range.Address > ulong.MaxValue - range.Length)
                {
                    return false;
                }
            }

            return true;
        }

        private static bool HasNonEmptyAcceleratorRanges(
            System.Collections.Generic.IReadOnlyList<AcceleratorMemoryRange>? ranges)
        {
            if (ranges is null || ranges.Count == 0)
            {
                return false;
            }

            for (int index = 0; index < ranges.Count; index++)
            {
                AcceleratorMemoryRange range = ranges[index];
                if (range.Length == 0 || range.Address > ulong.MaxValue - range.Length)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Resets this thread context (clears all compiled instructions and metadata).
        /// </summary>
        public void Reset()
        {
            _instructionCount = 0;
            Array.Clear(_instructionSlotMetadata, 0, _instructionSlotMetadata.Length);
            Array.Clear(_externalOperationIntents, 0, _externalOperationIntents.Length);
            Array.Clear(_externalOperationDescriptorIdentities, 0, _externalOperationDescriptorIdentities.Length);

            ResetIrMetadataDeclarations();
            InvalidateCanonicalCompileCache();
        }
    }
}
