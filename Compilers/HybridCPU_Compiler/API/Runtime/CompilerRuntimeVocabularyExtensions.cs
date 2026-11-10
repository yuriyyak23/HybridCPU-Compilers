using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.IR.Telemetry;
using HybridCPU_ISE.Arch;
using YAKSys_Hybrid_CPU.Core;

namespace HybridCPU.Compiler.Core.Runtime;

/// <summary>
/// Explicit source-compatibility helpers for legacy callers. The conversions are
/// vocabulary-only and do not transfer runtime admission or execution authority.
/// </summary>
public static class CompilerRuntimeVocabularyExtensions
{
    public static IrProgram BuildProgram(
        this HybridCpuIrBuilder builder,
        byte virtualThreadId,
        ReadOnlySpan<VLIW_Instruction> instructions,
        IReadOnlyList<IrLabelDeclaration>? labelDeclarations = null,
        IReadOnlyList<IrEntryPointDeclaration>? entryPointDeclarations = null,
        IReadOnlyList<IrInstructionSourceBinding>? instructionSourceBindings = null,
        IReadOnlyList<IrSectionDeclaration>? sectionDeclarations = null,
        IReadOnlyList<IrFunctionDeclaration>? functionDeclarations = null,
        VliwBundleAnnotations? bundleAnnotations = null,
        ulong domainTag = 0,
        IReadOnlyList<IrControlFlowTargetReference>? controlFlowTargetReferences = null) =>
        builder.BuildProgram(
            virtualThreadId,
            NativeTransportRuntimeAdapter.ToCore(instructions),
            labelDeclarations,
            entryPointDeclarations,
            instructionSourceBindings,
            sectionDeclarations,
            functionDeclarations,
            bundleAnnotations is null ? null : NativeTransportRuntimeAdapter.ToCore(bundleAnnotations),
            domainTag,
            controlFlowTargetReferences);

    public static double GetClassPressure(this TelemetryProfileReader reader, SlotClass slotClass) =>
        reader.GetClassPressure(NativeTransportRuntimeAdapter.ToCore(slotClass));

    public static double GetCertificatePressureByClass(this TelemetryProfileReader reader, SlotClass slotClass) =>
        reader.GetCertificatePressureByClass(NativeTransportRuntimeAdapter.ToCore(slotClass));

    public static long GetCertificateRejectCountByClass(this TelemetryProfileReader reader, SlotClass slotClass) =>
        reader.GetCertificateRejectCountByClass(NativeTransportRuntimeAdapter.ToCore(slotClass));

    public static double GetRejectRate(this TelemetryProfileReader reader, TypedSlotRejectReason reason) =>
        reader.GetRejectRate((IrTypedSlotRejectReason)(byte)reason);

    public static long GetHazardEffectCount(this TelemetryProfileReader reader, HazardEffectKind effect) =>
        reader.GetHazardEffectCount(NativeTransportRuntimeAdapter.ToCore(effect));

    public static double GetHazardEffectRate(this TelemetryProfileReader reader, HazardEffectKind effect) =>
        reader.GetHazardEffectRate(NativeTransportRuntimeAdapter.ToCore(effect));

    public static int GetClassPressurePenalty(this HybridCpuLocalListScheduler scheduler, SlotClass slotClass) =>
        scheduler.GetClassPressurePenalty(NativeTransportRuntimeAdapter.ToCore(slotClass));
}

public static class RuntimeSlotClassLaneMapAdapter
{
    public static int GetClassCapacity(IrSlotClass slotClass) =>
        SlotClassLaneMap.GetClassCapacity(NativeTransportRuntimeAdapter.ToRuntime(slotClass));

    public static byte GetLaneMask(IrSlotClass slotClass) =>
        SlotClassLaneMap.GetLaneMask(NativeTransportRuntimeAdapter.ToRuntime(slotClass));

    public static bool HasAliasedLanes(IrSlotClass slotClass) =>
        SlotClassLaneMap.HasAliasedLanes(NativeTransportRuntimeAdapter.ToRuntime(slotClass));

    public static IReadOnlyList<IrSlotClass> GetAliasedClasses(IrSlotClass slotClass)
    {
        ReadOnlySpan<SlotClass> runtimeClasses =
            SlotClassLaneMap.GetAliasedClasses(NativeTransportRuntimeAdapter.ToRuntime(slotClass));
        IrSlotClass[] result = new IrSlotClass[runtimeClasses.Length];
        for (int index = 0; index < result.Length; index++)
            result[index] = NativeTransportRuntimeAdapter.ToCore(runtimeClasses[index]);
        return result;
    }
}
