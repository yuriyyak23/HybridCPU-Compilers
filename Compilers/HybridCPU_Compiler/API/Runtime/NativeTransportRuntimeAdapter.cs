using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.IR.Matrix;
using HybridCPU.Compiler.Core.IR.Resources;
using HybridCPU.Compiler.Core.IR.Telemetry;
using HybridCPU_ISE.Arch;
using YAKSys_Hybrid_CPU;
using YAKSys_Hybrid_CPU.Core;
using YAKSys_Hybrid_CPU.Core.Execution.DmaStreamCompute;
using YAKSys_Hybrid_CPU.Core.Execution.ExternalAccelerators.Descriptors;
using YAKSys_Hybrid_CPU.Core.Contracts;
using YAKSys_Hybrid_CPU.Core.Registers;
using YAKSys_Hybrid_CPU.CloseToHSL.Core.ISA.Instructions.NonVmx.Lanes00_03Vector.MatrixTile;
using CoreLayoutPolicy = HybridCPU.Compiler.Core.IR.Matrix.MatrixTileLayoutPolicy;
using CoreNumericPolicy = HybridCPU.Compiler.Core.IR.Matrix.MatrixTileNumericPolicy;
using CoreMatrixDescriptor = HybridCPU.Compiler.Core.IR.Matrix.MatrixTileCanonicalDescriptorAbi;
using RuntimeLayoutPolicy = YAKSys_Hybrid_CPU.CloseToHSL.Core.ISA.Instructions.NonVmx.Lanes00_03Vector.MatrixTile.MatrixTileLayoutPolicy;
using RuntimeNumericPolicy = YAKSys_Hybrid_CPU.CloseToHSL.Core.ISA.Instructions.NonVmx.Lanes00_03Vector.MatrixTile.MatrixTileNumericPolicy;
using IR = HybridCPU.Compiler.Core.IR;
using CoreMatrix = HybridCPU.Compiler.Core.IR.Matrix;
using RuntimeMatrix = YAKSys_Hybrid_CPU.CloseToHSL.Core.ISA.Instructions.NonVmx.Lanes00_03Vector.MatrixTile;

namespace HybridCPU.Compiler.Core.Runtime;

/// <summary>
/// Explicit outward adapter between compiler-owned native carriers and the runtime transport vocabulary.
/// This type translates data only; it does not grant runtime legality or publication authority to Core.
/// </summary>
public static partial class NativeTransportRuntimeAdapter
{
    private const ulong RetiredWord3PolicyGapBitMask = 1UL << 50;

    public static HybridCpuCompiledProgram CompileProgram(
        byte virtualThreadId,
        ReadOnlySpan<VLIW_Instruction> instructions,
        IReadOnlyList<IrLabelDeclaration>? labelDeclarations = null,
        IReadOnlyList<IrEntryPointDeclaration>? entryPointDeclarations = null,
        FrontendMode frontendMode = FrontendMode.NativeVLIW,
        VliwBundleAnnotations? bundleAnnotations = null,
        ulong domainTag = 0,
        Action<string, string>? progressObserver = null,
        IReadOnlyList<IrControlFlowTargetReference>? controlFlowTargetReferences = null) =>
        HybridCpuCanonicalCompiler.CompileProgram(
            virtualThreadId,
            ToCore(instructions),
            labelDeclarations,
            entryPointDeclarations,
            (NativeFrontendMode)(byte)frontendMode,
            bundleAnnotations is null ? null : ToCore(bundleAnnotations),
            domainTag,
            progressObserver,
            controlFlowTargetReferences);

    public static HybridCpuCompilationMetricsResultV1 CompileProgramWithMetrics(
        byte virtualThreadId,
        ReadOnlySpan<VLIW_Instruction> instructions,
        CompilerScheduleMetricsRequestV1 metricsRequest,
        IReadOnlyList<IrLabelDeclaration>? labelDeclarations = null,
        IReadOnlyList<IrEntryPointDeclaration>? entryPointDeclarations = null,
        FrontendMode frontendMode = FrontendMode.NativeVLIW,
        VliwBundleAnnotations? bundleAnnotations = null,
        ulong domainTag = 0,
        Action<string, string>? progressObserver = null,
        IReadOnlyList<IrControlFlowTargetReference>? controlFlowTargetReferences = null) =>
        HybridCpuCanonicalCompiler.CompileProgramWithMetrics(
            virtualThreadId, ToCore(instructions), metricsRequest, labelDeclarations,
            entryPointDeclarations, (NativeFrontendMode)(byte)frontendMode,
            bundleAnnotations is null ? null : ToCore(bundleAnnotations), domainTag,
            progressObserver, controlFlowTargetReferences);

    public static HybridCpuCompilationResourceShadowResultV1 CompileProgramWithResourceShadow(
        byte virtualThreadId,
        ReadOnlySpan<VLIW_Instruction> instructions,
        IReadOnlyList<IrLabelDeclaration>? labelDeclarations = null,
        IReadOnlyList<IrEntryPointDeclaration>? entryPointDeclarations = null,
        FrontendMode frontendMode = FrontendMode.NativeVLIW,
        VliwBundleAnnotations? bundleAnnotations = null,
        ulong domainTag = 0,
        Action<string, string>? progressObserver = null,
        IReadOnlyList<IrControlFlowTargetReference>? controlFlowTargetReferences = null) =>
        HybridCpuCanonicalCompiler.CompileProgramWithResourceShadow(
            virtualThreadId, ToCore(instructions), labelDeclarations, entryPointDeclarations,
            (NativeFrontendMode)(byte)frontendMode,
            bundleAnnotations is null ? null : ToCore(bundleAnnotations), domainTag,
            progressObserver, controlFlowTargetReferences);

    public static HybridCpuCompiledProgram CompileProgramWithJointCycleComposition(
        byte virtualThreadId,
        ReadOnlySpan<VLIW_Instruction> instructions,
        HybridCpuCycleSearchOptionsV1? searchOptions = null,
        IReadOnlyList<IrLabelDeclaration>? labelDeclarations = null,
        IReadOnlyList<IrEntryPointDeclaration>? entryPointDeclarations = null,
        FrontendMode frontendMode = FrontendMode.NativeVLIW,
        VliwBundleAnnotations? bundleAnnotations = null,
        ulong domainTag = 0,
        Action<string, string>? progressObserver = null,
        IReadOnlyList<IrControlFlowTargetReference>? controlFlowTargetReferences = null) =>
        HybridCpuCanonicalCompiler.CompileProgramWithJointCycleComposition(
            virtualThreadId, ToCore(instructions), searchOptions, labelDeclarations,
            entryPointDeclarations, (NativeFrontendMode)(byte)frontendMode,
            bundleAnnotations is null ? null : ToCore(bundleAnnotations), domainTag,
            progressObserver, controlFlowTargetReferences);

    public static HybridCpuCompilationTopologyShadowResultV1 CompileProgramWithTopologyShadow(
        byte virtualThreadId,
        ReadOnlySpan<VLIW_Instruction> instructions,
        HybridCpuMachineTopologyV1? topology = null,
        IReadOnlyList<IrLabelDeclaration>? labelDeclarations = null,
        IReadOnlyList<IrEntryPointDeclaration>? entryPointDeclarations = null,
        FrontendMode frontendMode = FrontendMode.NativeVLIW,
        VliwBundleAnnotations? bundleAnnotations = null,
        ulong domainTag = 0,
        Action<string, string>? progressObserver = null,
        IReadOnlyList<IrControlFlowTargetReference>? controlFlowTargetReferences = null) =>
        HybridCpuCanonicalCompiler.CompileProgramWithTopologyShadow(
            virtualThreadId, ToCore(instructions), topology, labelDeclarations,
            entryPointDeclarations, (NativeFrontendMode)(byte)frontendMode,
            bundleAnnotations is null ? null : ToCore(bundleAnnotations), domainTag,
            progressObserver, controlFlowTargetReferences);

    public static HybridCpuInstructionWord ToCore(in VLIW_Instruction instruction) => new()
    {
        Word0 = instruction.Word0,
        Word1 = instruction.Word1,
        Word2 = instruction.Word2,
        // Runtime ingress may still carry the retired policy-gap bit. It has no
        // compiler meaning and must not enter the strict native Core carrier.
        Word3 = instruction.Word3 & ~RetiredWord3PolicyGapBitMask
    };

    public static HybridCpuInstructionWord[] ToCore(ReadOnlySpan<VLIW_Instruction> instructions)
    {
        HybridCpuInstructionWord[] result = new HybridCpuInstructionWord[instructions.Length];
        for (int index = 0; index < result.Length; index++) result[index] = ToCore(in instructions[index]);
        return result;
    }

    public static IrBundleAnnotations ToCore(VliwBundleAnnotations annotations)
    {
        ArgumentNullException.ThrowIfNull(annotations);
        IrInstructionSlotMetadata[] slots = new IrInstructionSlotMetadata[annotations.Count];
        for (int index = 0; index < slots.Length; index++)
        {
            if (!annotations.TryGetInstructionSlotMetadata(index, out InstructionSlotMetadata metadata))
            {
                slots[index] = IrInstructionSlotMetadata.Default;
                continue;
            }

            if (metadata == InstructionSlotMetadata.Default)
            {
                slots[index] = IrInstructionSlotMetadata.Default;
                continue;
            }

            SlotPlacementMetadata placement = metadata.SlotMetadata.AdmissionMetadata.Placement;
            slots[index] = new IrInstructionSlotMetadata(
                metadata.VirtualThreadId.Value,
                (IrSlotClass)(byte)placement.RequiredSlotClass,
                placement.PinningKind == SlotPinningKind.HardPinned
                    ? IrSlotBindingKind.HardPinned
                    : IrSlotBindingKind.ClassFlexible,
                placement.PinnedLaneId,
                placement.DomainTag,
                metadata.SlotMetadata.StealabilityPolicy == StealabilityPolicy.Stealable)
            {
                DmaStreamComputeDescriptor = metadata.DmaStreamComputeDescriptor,
                AcceleratorCommandDescriptor = metadata.AcceleratorCommandDescriptor,
                ExternalDescriptorEvidence = ProjectExternalDescriptorEvidence(metadata),
                MatrixTileNumericPolicy = metadata.MatrixTileNumericPolicy is RuntimeNumericPolicy numeric ? ToCore(numeric) : null,
                MatrixTileLayoutPolicy = metadata.MatrixTileLayoutPolicy is RuntimeLayoutPolicy layout ? ToCore(layout) : null
            };
        }
        return new IrBundleAnnotations(slots);
    }

    private static IrExternalDescriptorEvidenceV1? ProjectExternalDescriptorEvidence(
        InstructionSlotMetadata metadata)
    {
        if (metadata.DmaStreamComputeDescriptor is DmaStreamComputeDescriptor dsc)
        {
            if (dsc.NormalizedReadMemoryRanges.Any(static range => !CanProjectRange(range.Address, range.Length)) ||
                dsc.NormalizedWriteMemoryRanges.Any(static range => !CanProjectRange(range.Address, range.Length)))
                return null;
            return new IrExternalDescriptorEvidenceV1(
                "hybridcpu.dsc-exact-footprint/v1",
                dsc.DescriptorIdentityHash,
                dsc.PartialCompletionPolicy == DmaStreamComputePartialCompletionPolicy.AllOrNone,
                dsc.NormalizedReadMemoryRanges.Select(static range =>
                    new IrMemoryRegion(range.Address, checked((uint)range.Length), false)).ToArray(),
                dsc.NormalizedWriteMemoryRanges.Select(static range =>
                    new IrMemoryRegion(range.Address, checked((uint)range.Length), true)).ToArray());
        }
        if (metadata.AcceleratorCommandDescriptor is AcceleratorCommandDescriptor accelerator)
        {
            if (accelerator.NormalizedFootprint.SourceRanges.Any(static range => !CanProjectRange(range.Address, range.Length)) ||
                accelerator.NormalizedFootprint.DestinationRanges.Any(static range => !CanProjectRange(range.Address, range.Length)) ||
                accelerator.NormalizedFootprint.ScratchRanges.Any(static range => !CanProjectRange(range.Address, range.Length)))
                return null;
            return new IrExternalDescriptorEvidenceV1(
                "hybridcpu.l7-exact-footprint/v1",
                accelerator.Identity.DescriptorIdentityHash,
                accelerator.PartialCompletionPolicy == AcceleratorPartialCompletionPolicy.AllOrNone,
                accelerator.NormalizedFootprint.SourceRanges
                    .Concat(accelerator.NormalizedFootprint.ScratchRanges)
                    .Select(static range => new IrMemoryRegion(range.Address, checked((uint)range.Length), false)).ToArray(),
                accelerator.NormalizedFootprint.DestinationRanges
                    .Concat(accelerator.NormalizedFootprint.ScratchRanges)
                    .Select(static range => new IrMemoryRegion(range.Address, checked((uint)range.Length), true)).ToArray());
        }
        return null;
    }

    private static bool CanProjectRange(ulong address, ulong length) =>
        length is > 0 and <= uint.MaxValue && address <= ulong.MaxValue - length;

    public static IrBundleAnnotations[] ToCore(IReadOnlyList<VliwBundleAnnotations> annotations)
    {
        ArgumentNullException.ThrowIfNull(annotations);
        IrBundleAnnotations[] result = new IrBundleAnnotations[annotations.Count];
        for (int index = 0; index < result.Length; index++) result[index] = ToCore(annotations[index]);
        return result;
    }

    public static HybridCpuInstructionBundle ToCore(in VLIW_Bundle bundle)
    {
        HybridCpuInstructionBundle result = new HybridCpuInstructionBundle();
        for (int index = 0; index < 8; index++)
        {
            VLIW_Instruction instruction = bundle.GetInstruction(index);
            result.SetInstruction(index, ToCore(in instruction));
        }
        return result;
    }

    public static VLIW_Instruction ToRuntime(in HybridCpuInstructionWord instruction) => new()
    {
        Word0 = instruction.Word0,
        Word1 = instruction.Word1,
        Word2 = instruction.Word2,
        Word3 = instruction.Word3
    };

    public static VLIW_Bundle ToRuntime(in HybridCpuInstructionBundle bundle)
    {
        VLIW_Bundle result = new VLIW_Bundle();
        for (int index = 0; index < 8; index++)
        {
            HybridCpuInstructionWord instruction = bundle.GetInstruction(index);
            result.SetInstruction(index, ToRuntime(in instruction));
        }
        return result;
    }

    public static VLIW_Bundle[] ToRuntime(IReadOnlyList<HybridCpuInstructionBundle> bundles)
    {
        ArgumentNullException.ThrowIfNull(bundles);
        VLIW_Bundle[] result = new VLIW_Bundle[bundles.Count];
        for (int index = 0; index < result.Length; index++)
        {
            HybridCpuInstructionBundle bundle = bundles[index];
            result[index] = ToRuntime(in bundle);
        }
        return result;
    }

    public static VliwBundleAnnotations[] ToRuntime(IReadOnlyList<IrBundleAnnotations> annotations)
    {
        ArgumentNullException.ThrowIfNull(annotations);
        VliwBundleAnnotations[] result = new VliwBundleAnnotations[annotations.Count];
        for (int index = 0; index < result.Length; index++) result[index] = ToRuntime(annotations[index]);
        return result;
    }

    public static HybridCpuDataType ToCore(DataTypeEnum value) => (HybridCpuDataType)(byte)value;

    public static DataTypeEnum ToRuntime(HybridCpuDataType value) => (DataTypeEnum)(byte)value;

    public static HybridCpuOpcode ToCore(Processor.CPU_Core.InstructionsEnum value) =>
        (HybridCpuOpcode)(uint)value;

    public static HybridCpuOpcode[] ToCore(IReadOnlyList<Processor.CPU_Core.InstructionsEnum> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        HybridCpuOpcode[] result = new HybridCpuOpcode[values.Count];
        for (int index = 0; index < result.Length; index++) result[index] = ToCore(values[index]);
        return result;
    }

    public static Processor.CPU_Core.InstructionsEnum ToRuntime(HybridCpuOpcode value) =>
        (Processor.CPU_Core.InstructionsEnum)(uint)value;

    public static IrSlotClass ToCore(SlotClass value) => (IrSlotClass)(byte)value;

    public static SlotClass ToRuntime(IrSlotClass value) => (SlotClass)(byte)value;

    public static SlotPinningKind ToRuntimePinningKind(IrSlotBindingKind value) => value switch
    {
        IrSlotBindingKind.HardPinned => SlotPinningKind.HardPinned,
        IrSlotBindingKind.ClassFlexible or IrSlotBindingKind.SingletonClass => SlotPinningKind.ClassFlexible,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown compiler slot binding kind.")
    };

    public static IrTypedSlotPolicyMode ToCore(CompilerTypedSlotPolicyMode value) =>
        (IrTypedSlotPolicyMode)(byte)value;

    public static CompilerTypedSlotPolicyMode ToRuntime(IrTypedSlotPolicyMode value) =>
        (CompilerTypedSlotPolicyMode)(byte)value;

    public static IrHazardEffectKind ToCore(HazardEffectKind value) => (IrHazardEffectKind)(byte)value;

    public static HazardEffectKind ToRuntime(IrHazardEffectKind value) => (HazardEffectKind)(byte)value;

    public static IrSafetyMask128 ToCore(SafetyMask128 value) => new(value.Low, value.High);

    public static SafetyMask128 ToRuntime(IrSafetyMask128 value) => new(value.Low, value.High);

    public static IrTypedSlotBundleFacts ToCore(TypedSlotBundleFacts value) => new()
    {
        Slot0Class = ToCore(value.Slot0Class),
        Slot1Class = ToCore(value.Slot1Class),
        Slot2Class = ToCore(value.Slot2Class),
        Slot3Class = ToCore(value.Slot3Class),
        Slot4Class = ToCore(value.Slot4Class),
        Slot5Class = ToCore(value.Slot5Class),
        Slot6Class = ToCore(value.Slot6Class),
        Slot7Class = ToCore(value.Slot7Class),
        PinningKindMask = value.PinningKindMask,
        FlexibleOpCount = value.FlexibleOpCount,
        PinnedOpCount = value.PinnedOpCount,
        AluCount = value.AluCount,
        LsuCount = value.LsuCount,
        DmaStreamCount = value.DmaStreamCount,
        MatrixTileStreamCount = value.MatrixTileStreamCount,
        BranchControlCount = value.BranchControlCount,
        SystemSingletonCount = value.SystemSingletonCount
    };

    public static IrTypedSlotBundleFacts[] ToCore(IReadOnlyList<TypedSlotBundleFacts> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        IrTypedSlotBundleFacts[] result = new IrTypedSlotBundleFacts[values.Count];
        for (int index = 0; index < result.Length; index++) result[index] = ToCore(values[index]);
        return result;
    }

    public static TypedSlotBundleFacts ToRuntime(IrTypedSlotBundleFacts value) => new()
    {
        Slot0Class = ToRuntime(value.Slot0Class),
        Slot1Class = ToRuntime(value.Slot1Class),
        Slot2Class = ToRuntime(value.Slot2Class),
        Slot3Class = ToRuntime(value.Slot3Class),
        Slot4Class = ToRuntime(value.Slot4Class),
        Slot5Class = ToRuntime(value.Slot5Class),
        Slot6Class = ToRuntime(value.Slot6Class),
        Slot7Class = ToRuntime(value.Slot7Class),
        PinningKindMask = value.PinningKindMask,
        FlexibleOpCount = value.FlexibleOpCount,
        PinnedOpCount = value.PinnedOpCount,
        AluCount = value.AluCount,
        LsuCount = value.LsuCount,
        DmaStreamCount = value.DmaStreamCount,
        MatrixTileStreamCount = value.MatrixTileStreamCount,
        BranchControlCount = value.BranchControlCount,
        SystemSingletonCount = value.SystemSingletonCount
    };

    public static TypedSlotBundleFacts[] ToRuntime(IReadOnlyList<IrTypedSlotBundleFacts> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        TypedSlotBundleFacts[] result = new TypedSlotBundleFacts[values.Count];
        for (int index = 0; index < result.Length; index++) result[index] = ToRuntime(values[index]);
        return result;
    }

    public static CoreMatrixDescriptor ToCore(RuntimeMatrix.MatrixTileCanonicalDescriptorAbi value) => new(
        value.Rows,
        value.Columns,
        value.ElementSizeBytes,
        value.StrideBytes,
        (CoreMatrix.MatrixTileDescriptorLayoutKind)(byte)value.Layout);

    public static RuntimeMatrix.MatrixTileCanonicalDescriptorAbi ToRuntime(CoreMatrixDescriptor value) => new(
        value.Rows,
        value.Columns,
        value.ElementSizeBytes,
        value.StrideBytes,
        (RuntimeMatrix.MatrixTileDescriptorLayoutKind)(byte)value.Layout);

    public static VliwBundleAnnotations ToRuntime(IrBundleAnnotations annotations)
    {
        ArgumentNullException.ThrowIfNull(annotations);
        InstructionSlotMetadata[] slots = new InstructionSlotMetadata[annotations.Count];
        for (int index = 0; index < slots.Length; index++)
        {
            if (!annotations.TryGetInstructionSlotMetadata(index, out IrInstructionSlotMetadata metadata))
            {
                slots[index] = InstructionSlotMetadata.Default;
                continue;
            }

            if (metadata == IrInstructionSlotMetadata.Default)
            {
                slots[index] = InstructionSlotMetadata.Default;
                continue;
            }

            SlotPinningKind pinningKind = metadata.BindingKind == IrSlotBindingKind.HardPinned
                ? SlotPinningKind.HardPinned
                : SlotPinningKind.ClassFlexible;
            SlotPlacementMetadata placement = new SlotPlacementMetadata
            {
                RequiredSlotClass = (SlotClass)(byte)metadata.RequiredSlotClass,
                PinningKind = pinningKind,
                PinnedLaneId = metadata.PinnedLaneId,
                DomainTag = metadata.DomainTag
            };
            slots[index] = new InstructionSlotMetadata(
                VtId.Create(metadata.VirtualThreadId),
                new SlotMetadata
                {
                    StealabilityPolicy = metadata.Stealable ? StealabilityPolicy.Stealable : StealabilityPolicy.NotStealable,
                    AdmissionMetadata = MicroOpAdmissionMetadata.Default with
                    {
                        IsStealable = metadata.Stealable,
                        DomainTag = metadata.DomainTag,
                        Placement = placement
                    }
                })
            {
                DmaStreamComputeDescriptor = metadata.DmaStreamComputeDescriptor as DmaStreamComputeDescriptor,
                AcceleratorCommandDescriptor = metadata.AcceleratorCommandDescriptor as AcceleratorCommandDescriptor,
                MatrixTileNumericPolicy = metadata.MatrixTileNumericPolicy is CoreNumericPolicy numeric ? ToRuntime(numeric) : null,
                MatrixTileLayoutPolicy = metadata.MatrixTileLayoutPolicy is CoreLayoutPolicy layout ? ToRuntime(layout) : null
            };
        }
        return new VliwBundleAnnotations(slots);
    }

    public static CoreNumericPolicy ToCore(RuntimeNumericPolicy value) => new(
        value.AbiVersion, (CoreMatrix.MatrixTileNumericProfileId)(byte)value.ProfileId,
        (HybridCpuDataType)(byte)value.ElementType, (HybridCpuDataType)(byte)value.AccumulatorType,
        (HybridCpuDataType)(byte)value.PublishFormat, (CoreMatrix.MatrixTileNumericSignedness)(byte)value.Signedness,
        (CoreMatrix.MatrixTileNumericWideningRule)(byte)value.WideningRule, (CoreMatrix.MatrixTileNumericMultiplyRule)(byte)value.MultiplyRule,
        (CoreMatrix.MatrixTileNumericAddRule)(byte)value.AddRule, (CoreMatrix.MatrixTileNumericRoundingMode)(byte)value.RoundingMode,
        (CoreMatrix.MatrixTileNumericSaturationMode)(byte)value.SaturationMode, (CoreMatrix.MatrixTileNumericOverflowMode)(byte)value.OverflowMode,
        (CoreMatrix.MatrixTileNumericNaNPolicy)(byte)value.NaNPolicy, (CoreMatrix.MatrixTileNumericInfinityPolicy)(byte)value.InfinityPolicy,
        (CoreMatrix.MatrixTileNumericDenormalPolicy)(byte)value.DenormalPolicy, (CoreMatrix.MatrixTileNumericReproducibilityMode)(byte)value.ReproducibilityMode,
        (CoreMatrix.MatrixTileNumericExceptionPolicy)(byte)value.ExceptionPolicy, value.Fingerprint);

    public static RuntimeNumericPolicy ToRuntime(CoreNumericPolicy value) => new(
        value.AbiVersion, (RuntimeMatrix.MatrixTileNumericProfileId)(byte)value.ProfileId,
        (DataTypeEnum)(byte)value.ElementType, (DataTypeEnum)(byte)value.AccumulatorType,
        (DataTypeEnum)(byte)value.PublishFormat, (RuntimeMatrix.MatrixTileNumericSignedness)(byte)value.Signedness,
        (RuntimeMatrix.MatrixTileNumericWideningRule)(byte)value.WideningRule, (RuntimeMatrix.MatrixTileNumericMultiplyRule)(byte)value.MultiplyRule,
        (RuntimeMatrix.MatrixTileNumericAddRule)(byte)value.AddRule, (RuntimeMatrix.MatrixTileNumericRoundingMode)(byte)value.RoundingMode,
        (RuntimeMatrix.MatrixTileNumericSaturationMode)(byte)value.SaturationMode, (RuntimeMatrix.MatrixTileNumericOverflowMode)(byte)value.OverflowMode,
        (RuntimeMatrix.MatrixTileNumericNaNPolicy)(byte)value.NaNPolicy, (RuntimeMatrix.MatrixTileNumericInfinityPolicy)(byte)value.InfinityPolicy,
        (RuntimeMatrix.MatrixTileNumericDenormalPolicy)(byte)value.DenormalPolicy, (RuntimeMatrix.MatrixTileNumericReproducibilityMode)(byte)value.ReproducibilityMode,
        (RuntimeMatrix.MatrixTileNumericExceptionPolicy)(byte)value.ExceptionPolicy, value.Fingerprint);

    public static CoreLayoutPolicy ToCore(RuntimeLayoutPolicy value) => new(
        value.AbiVersion, (CoreMatrix.MatrixTileLayoutProfileId)(byte)value.ProfileId,
        (CoreMatrix.MatrixTileProjectedOperationKind)(byte)value.OperationKind,
        (CoreMatrix.MatrixTileElementAddressingKind)(byte)value.SourceAddressing,
        (CoreMatrix.MatrixTileElementAddressingKind)(byte)value.SecondaryAddressing,
        (CoreMatrix.MatrixTileElementAddressingKind)(byte)value.DestinationAddressing,
        (CoreMatrix.MatrixTileKIterationOrderKind)(byte)value.KIterationOrder,
        (CoreMatrix.MatrixTileTransposePermutationKind)(byte)value.TransposePermutation,
        (CoreMatrix.MatrixTileTransposeAliasPolicyKind)(byte)value.TransposeAliasPolicy, value.Fingerprint);

    public static RuntimeLayoutPolicy ToRuntime(CoreLayoutPolicy value) => new(
        value.AbiVersion, (RuntimeMatrix.MatrixTileLayoutProfileId)(byte)value.ProfileId,
        (RuntimeMatrix.MatrixTileProjectedOperationKind)(byte)value.OperationKind,
        (RuntimeMatrix.MatrixTileElementAddressingKind)(byte)value.SourceAddressing,
        (RuntimeMatrix.MatrixTileElementAddressingKind)(byte)value.SecondaryAddressing,
        (RuntimeMatrix.MatrixTileElementAddressingKind)(byte)value.DestinationAddressing,
        (RuntimeMatrix.MatrixTileKIterationOrderKind)(byte)value.KIterationOrder,
        (RuntimeMatrix.MatrixTileTransposePermutationKind)(byte)value.TransposePermutation,
        (RuntimeMatrix.MatrixTileTransposeAliasPolicyKind)(byte)value.TransposeAliasPolicy, value.Fingerprint);

}
