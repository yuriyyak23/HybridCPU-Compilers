using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HybridCPU.Compiler.Core.IR.Telemetry;

/// <summary>
/// Evidence quality for one observational compiler metric.
/// </summary>
public enum CompilerMetricEvidenceQualityV1 : byte
{
    Unavailable = 0,
    Measured = 1,
    HistoricalUnverified = 2
}

/// <summary>
/// Opt-in request for the version-one compiler scheduling metrics projection.
/// Identity strings are fingerprinted before they enter the result.
/// </summary>
public sealed record CompilerScheduleMetricsRequestV1(
    bool Enabled = true,
    string ProfileIdentity = "absent",
    string ModelIdentity = "HybridCPU-W8-existing-structural-model",
    string OptionsIdentity = "canonical-defaults");

/// <summary>
/// One deterministic ready-window observation made before cycle-group selection.
/// </summary>
public sealed record CompilerReadyWindowSampleV1(int BlockId, int Cycle, int Size);

/// <summary>
/// Deterministic metrics for one current basic-block scheduling region.
/// Region identity is deliberately a projection of the current BB identity; Phase 00 does not
/// introduce scheduling regions or authorize motion across block boundaries.
/// </summary>
public sealed record CompilerScheduleBlockMetricsV1(
    int BlockId,
    string RegionIdentity,
    ulong StartAddress,
    ulong EndAddress,
    int InstructionCount,
    int ScheduleCycles,
    int BundleCount,
    int MaxSingleVtWidth,
    int MaxPackedVtWidth,
    int MaxReadyWindowSize);

/// <summary>
/// Versioned, deterministic, compiler-owned observation of scheduling and bundle formation.
/// This type is diagnostic evidence only. It is not runtime legality, execution, publication,
/// commit, or retire authority.
/// </summary>
public sealed record CompilerScheduleMetricsV1(
    string Schema,
    string InputFingerprint,
    string ProfileFingerprint,
    string ModelFingerprint,
    string OptionsFingerprint,
    string ScheduleFingerprint,
    string BundleFingerprint,
    int ScheduleCycles,
    int BundleCount,
    int MaxSingleVtWidth,
    int MaxPackedVtWidth,
    int MaxReadyWindowSize,
    CompilerMetricEvidenceQualityV1 ReadyWindowEvidenceQuality,
    long SchedulerCandidateEvaluatedCount,
    long SchedulerCandidatePrunedCount,
    long PlacementEvaluatedCount,
    long PlacementParetoOptimalCount,
    long PlacementDominatedCount,
    long? PlacementPrunedCount,
    CompilerMetricEvidenceQualityV1 PlacementPrunedEvidenceQuality,
    int SchedulerCapHitCount,
    int PlacementCapHitCount,
    IReadOnlyList<string> FallbackReasons,
    IReadOnlyList<CompilerReadyWindowSampleV1> ReadyWindowSamples,
    IReadOnlyList<CompilerScheduleBlockMetricsV1> Blocks)
{
    public const string SchemaName = "CompilerScheduleMetricsV1";

    /// <summary>
    /// Serializes the deterministic metrics payload with stable field and collection ordering.
    /// Wall-clock and managed-memory samples intentionally live in a separate telemetry record.
    /// </summary>
    public byte[] ToDeterministicJsonBytes() =>
        JsonSerializer.SerializeToUtf8Bytes(this, CompilerScheduleMetricsJsonV1.Options);

    public string ToDeterministicJson() => Encoding.UTF8.GetString(ToDeterministicJsonBytes());
}

/// <summary>
/// Non-deterministic compile-resource sample. It is never included in the deterministic schedule
/// metrics payload and must never be read by scheduling, placement, or lowering policy.
/// </summary>
public sealed record CompilerCompileResourceTelemetryV1(
    long ElapsedTimestampTicks,
    long TimestampFrequency,
    long ObservedPeakManagedMemoryBytes,
    CompilerMetricEvidenceQualityV1 EvidenceQuality)
{
    internal static CompilerCompileResourceTelemetryV1 Measure(
        long startTimestamp,
        long startManagedMemoryBytes)
    {
        long endTimestamp = Stopwatch.GetTimestamp();
        long endManagedMemoryBytes = GC.GetTotalMemory(forceFullCollection: false);
        return new CompilerCompileResourceTelemetryV1(
            Math.Max(0, endTimestamp - startTimestamp),
            Stopwatch.Frequency,
            Math.Max(startManagedMemoryBytes, endManagedMemoryBytes),
            CompilerMetricEvidenceQualityV1.Measured);
    }
}

/// <summary>
/// Opt-in result projection. The compiled program is the unchanged canonical artifact; metrics
/// and resource telemetry are absent when the request is disabled.
/// </summary>
public sealed record HybridCpuCompilationMetricsResultV1(
    HybridCpuCompiledProgram CompiledProgram,
    CompilerScheduleMetricsV1? ScheduleMetrics,
    CompilerCompileResourceTelemetryV1? ResourceTelemetry);

/// <summary>
/// Per-compilation collector shared by the canonical compiler, scheduler, and bundle former.
/// Recording failures are swallowed so telemetry cannot alter compiler behavior.
/// </summary>
public sealed class CompilerScheduleMetricsCollectorV1
{
    private string _inputFingerprint;
    private readonly string _profileFingerprint;
    private readonly string _modelFingerprint;
    private readonly string _optionsFingerprint;
    private readonly List<CompilerReadyWindowSampleV1> _readyWindowSamples = new();
    private readonly SortedSet<string> _fallbackReasons = new(StringComparer.Ordinal);
    private long _schedulerCandidateEvaluatedCount;
    private long _schedulerCandidatePrunedCount;
    private int _schedulerCapHitCount;
    private int _placementCapHitCount;

    private CompilerScheduleMetricsCollectorV1(
        string inputFingerprint,
        CompilerScheduleMetricsRequestV1 request)
    {
        _inputFingerprint = inputFingerprint;
        _profileFingerprint = CompilerScheduleFingerprintV1.HashIdentity(request.ProfileIdentity);
        _modelFingerprint = CompilerScheduleFingerprintV1.HashIdentity(request.ModelIdentity);
        _optionsFingerprint = CompilerScheduleFingerprintV1.HashIdentity(request.OptionsIdentity);
    }

    public static CompilerScheduleMetricsCollectorV1 CreateForInput(
        byte virtualThreadId,
        ReadOnlySpan<HybridCpuInstructionWord> instructions,
        CompilerScheduleMetricsRequestV1 request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new CompilerScheduleMetricsCollectorV1(
            CompilerScheduleFingerprintV1.HashInput(virtualThreadId, instructions),
            request);
    }

    public static CompilerScheduleMetricsCollectorV1 CreateForProgram(
        IrProgram program,
        CompilerScheduleMetricsRequestV1 request)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(request);
        return new CompilerScheduleMetricsCollectorV1(
            CompilerScheduleFingerprintV1.HashProgramInput(program),
            request);
    }

    public CompilerScheduleMetricsV1 Complete(HybridCpuCompiledProgram compiledProgram)
    {
        ArgumentNullException.ThrowIfNull(compiledProgram);
        return Complete(compiledProgram.ProgramSchedule, compiledProgram.BundleLayout);
    }

    internal void CaptureProgramInput(IrProgram program) =>
        TryRecord(() => _inputFingerprint = CompilerScheduleFingerprintV1.HashProgramInput(program));

    public CompilerScheduleMetricsV1 Complete(
        IrProgramSchedule programSchedule,
        IrProgramBundlingResult bundlingResult)
    {
        ArgumentNullException.ThrowIfNull(programSchedule);
        ArgumentNullException.ThrowIfNull(bundlingResult);

        List<CompilerScheduleBlockMetricsV1> blockMetrics = new List<CompilerScheduleBlockMetricsV1>(programSchedule.BlockSchedules.Count);
        int scheduleCycles = 0;
        int bundleCount = 0;
        int maxSingleVtWidth = 0;
        int maxPackedVtWidth = 0;
        int maxReadyWindowSize = 0;
        long placementEvaluatedCount = 0;
        long placementParetoOptimalCount = 0;
        long placementDominatedCount = 0;

        foreach (IrBasicBlockSchedule blockSchedule in programSchedule.BlockSchedules)
        {
            if (!bundlingResult.TryGetBlockResult(blockSchedule.BlockId, out IrBasicBlockBundlingResult? blockResult)
                || blockResult is null)
            {
                throw new InvalidOperationException($"Missing bundling result for block {blockSchedule.BlockId}.");
            }

            int blockSingleVtWidth = 0;
            int blockPackedVtWidth = 0;
            foreach (IrMaterializedBundle bundle in blockResult.Bundles)
            {
                int packedWidth = bundle.IssuedInstructionCount;
                int singleVtWidth = bundle.CycleGroup.Instructions
                    .GroupBy(static instruction => instruction.VirtualThreadId)
                    .Select(static group => group.Count())
                    .DefaultIfEmpty(0)
                    .Max();
                blockSingleVtWidth = Math.Max(blockSingleVtWidth, singleVtWidth);
                blockPackedVtWidth = Math.Max(blockPackedVtWidth, packedWidth);
                placementEvaluatedCount += bundle.PlacementSearchSummary.EvaluatedPlacementCount;
                placementParetoOptimalCount += bundle.PlacementSearchSummary.ParetoOptimalPlacementCount;
                placementDominatedCount += bundle.PlacementSearchSummary.DominatedPlacementCount;
            }

            int blockReadyWindowSize = _readyWindowSamples
                .Where(sample => sample.BlockId == blockSchedule.BlockId)
                .Select(static sample => sample.Size)
                .DefaultIfEmpty(0)
                .Max();
            scheduleCycles += blockSchedule.ScheduleLength;
            bundleCount += blockResult.Bundles.Count;
            maxSingleVtWidth = Math.Max(maxSingleVtWidth, blockSingleVtWidth);
            maxPackedVtWidth = Math.Max(maxPackedVtWidth, blockPackedVtWidth);
            maxReadyWindowSize = Math.Max(maxReadyWindowSize, blockReadyWindowSize);
            blockMetrics.Add(new CompilerScheduleBlockMetricsV1(
                blockSchedule.BlockId,
                $"bb:{blockSchedule.BlockId}",
                blockSchedule.Block.StartAddress,
                blockSchedule.Block.EndAddress,
                blockSchedule.Block.Instructions.Count,
                blockSchedule.ScheduleLength,
                blockResult.Bundles.Count,
                blockSingleVtWidth,
                blockPackedVtWidth,
                blockReadyWindowSize));
        }

        CompilerReadyWindowSampleV1[] readyWindowSamples = _readyWindowSamples
            .OrderBy(static sample => sample.BlockId)
            .ThenBy(static sample => sample.Cycle)
            .ThenBy(static sample => sample.Size)
            .ToArray();

        return new CompilerScheduleMetricsV1(
            CompilerScheduleMetricsV1.SchemaName,
            _inputFingerprint,
            _profileFingerprint,
            _modelFingerprint,
            _optionsFingerprint,
            CompilerScheduleFingerprintV1.HashSchedule(programSchedule),
            CompilerScheduleFingerprintV1.HashBundles(bundlingResult),
            scheduleCycles,
            bundleCount,
            maxSingleVtWidth,
            maxPackedVtWidth,
            maxReadyWindowSize,
            readyWindowSamples.Length > 0
                ? CompilerMetricEvidenceQualityV1.Measured
                : CompilerMetricEvidenceQualityV1.Unavailable,
            _schedulerCandidateEvaluatedCount,
            _schedulerCandidatePrunedCount,
            placementEvaluatedCount,
            placementParetoOptimalCount,
            placementDominatedCount,
            PlacementPrunedCount: null,
            CompilerMetricEvidenceQualityV1.Unavailable,
            _schedulerCapHitCount,
            _placementCapHitCount,
            _fallbackReasons.ToArray(),
            readyWindowSamples,
            blockMetrics);
    }

    internal void RecordReadyWindow(int blockId, int cycle, int size) =>
        TryRecord(() => _readyWindowSamples.Add(new CompilerReadyWindowSampleV1(blockId, cycle, size)));

    internal void RecordSchedulerCandidate(bool structurallyPruned) => TryRecord(() =>
    {
        _schedulerCandidateEvaluatedCount++;
        if (structurallyPruned)
        {
            _schedulerCandidatePrunedCount++;
        }
    });

    internal void RecordFallback(string reason) =>
        TryRecord(() => _fallbackReasons.Add(reason));

    internal void RecordSchedulerCapHit(string reason) => TryRecord(() =>
    {
        _schedulerCapHitCount++;
        _fallbackReasons.Add(reason);
    });

    internal void RecordPlacementCapHit(string reason) => TryRecord(() =>
    {
        _placementCapHitCount++;
        _fallbackReasons.Add(reason);
    });

    private static void TryRecord(Action record)
    {
        try
        {
            record();
        }
        catch
        {
            // Phase 00 telemetry is fail-open and observational by contract.
        }
    }
}

/// <summary>
/// Stable SHA-256 fingerprints for Phase 00 scheduling observations.
/// </summary>
public static class CompilerScheduleFingerprintV1
{
    public static string HashIdentity(string? identity) =>
        HashBytes(Encoding.UTF8.GetBytes(identity ?? "absent"));

    public static string HashInput(byte virtualThreadId, ReadOnlySpan<HybridCpuInstructionWord> instructions)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendByte(hash, virtualThreadId);
        AppendInt32(hash, instructions.Length);
        Span<byte> encodedInstruction = stackalloc byte[32];
        foreach (HybridCpuInstructionWord instruction in instructions)
        {
            if (!instruction.TryWriteBytes(encodedInstruction))
            {
                throw new InvalidOperationException("VLIW instruction did not fit its canonical 32-byte carrier.");
            }

            hash.AppendData(encodedInstruction);
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    public static string HashProgramInput(IrProgram program)
    {
        ArgumentNullException.ThrowIfNull(program);
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendByte(hash, program.VirtualThreadId);
        AppendInt32(hash, program.Instructions.Count);
        foreach (IrInstruction instruction in program.Instructions.OrderBy(static instruction => instruction.Index))
        {
            AppendInstructionIdentity(hash, instruction);
        }

        AppendInt32(hash, program.BasicBlocks.Count);
        foreach (IrBasicBlock block in program.BasicBlocks.OrderBy(static block => block.Id))
        {
            AppendInt32(hash, block.Id);
            AppendInt32(hash, block.StartInstructionIndex);
            AppendInt32(hash, block.EndInstructionIndex);
            AppendUInt64(hash, block.StartAddress);
            AppendUInt64(hash, block.EndAddress);
            AppendBool(hash, block.HasUnresolvedControlTransfer);
            AppendBool(hash, block.ExitBlock);
            AppendBool(hash, block.BarrierBoundary);
            AppendString(hash, block.PrimaryLabel);
            AppendString(hash, block.SectionName);
            AppendString(hash, block.FunctionName);
            AppendInt32Sequence(hash, block.PredecessorBlockIds);
            AppendInt32Sequence(hash, block.SuccessorBlockIds);
            AppendStringSequence(hash, block.LabelNames);
        }

        AppendInt32(hash, program.ControlFlowGraph.Edges.Count);
        foreach (IrControlFlowEdge edge in program.ControlFlowGraph.Edges
                     .OrderBy(static edge => edge.SourceBlockId)
                     .ThenBy(static edge => edge.TargetBlockId)
                     .ThenBy(static edge => edge.Kind))
        {
            AppendInt32(hash, edge.SourceBlockId);
            AppendInt32(hash, edge.TargetBlockId);
            AppendInt32(hash, (int)edge.Kind);
        }

        AppendInt32(hash, program.Labels.Count);
        foreach (IrProgramLabel label in program.Labels
                     .OrderBy(static label => label.InstructionIndex)
                     .ThenBy(static label => label.Name, StringComparer.Ordinal))
        {
            AppendString(hash, label.Name);
            AppendInt32(hash, label.InstructionIndex);
            AppendUInt64(hash, label.Address);
            AppendInt32(hash, label.BlockId);
            AppendBool(hash, label.IsSynthetic);
            AppendBool(hash, label.IsEntryLabel);
            AppendString(hash, label.SectionName);
            AppendString(hash, label.FunctionName);
        }

        AppendInt32(hash, program.EntryPoints.Count);
        foreach (IrEntryPointMetadata entryPoint in program.EntryPoints
                     .OrderBy(static entryPoint => entryPoint.InstructionIndex)
                     .ThenBy(static entryPoint => entryPoint.Name, StringComparer.Ordinal))
        {
            AppendString(hash, entryPoint.Name);
            AppendInt32(hash, (int)entryPoint.Kind);
            AppendInt32(hash, entryPoint.InstructionIndex);
            AppendUInt64(hash, entryPoint.Address);
            AppendInt32(hash, entryPoint.BlockId);
            AppendBool(hash, entryPoint.IsSynthetic);
            AppendString(hash, entryPoint.SectionName);
            AppendString(hash, entryPoint.FunctionName);
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    public static string HashSchedule(IrProgramSchedule schedule)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendInt32(hash, schedule.BlockSchedules.Count);
        foreach (IrBasicBlockSchedule block in schedule.BlockSchedules)
        {
            AppendInt32(hash, block.BlockId);
            AppendInt32(hash, block.ScheduleLength);
            AppendInt32(hash, block.ScheduledInstructions.Count);
            foreach (IrScheduledInstruction instruction in block.ScheduledInstructions
                         .OrderBy(static instruction => instruction.Cycle)
                         .ThenBy(static instruction => instruction.OrderInCycle)
                         .ThenBy(static instruction => instruction.InstructionIndex))
            {
                AppendInt32(hash, instruction.InstructionIndex);
                AppendInt32(hash, instruction.Cycle);
                AppendInt32(hash, instruction.OrderInCycle);
                AppendInt32(hash, instruction.ReadyCycle);
            }
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    public static string HashBundles(IrProgramBundlingResult bundling)
    {
        ArgumentNullException.ThrowIfNull(bundling);
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendInt32(hash, bundling.BlockResults.Count);
        foreach (IrBasicBlockBundlingResult block in bundling.BlockResults)
        {
            AppendInt32(hash, block.BlockId);
            AppendInt32(hash, block.Bundles.Count);
            foreach (IrMaterializedBundle bundle in block.Bundles)
            {
                AppendInt32(hash, bundle.Cycle);
                AppendInt32(hash, bundle.Slots.Count);
                foreach (IrMaterializedBundleSlot slot in bundle.Slots.OrderBy(static slot => slot.SlotIndex))
                {
                    AppendInt32(hash, slot.SlotIndex);
                    AppendInt32(hash, slot.Instruction?.Index ?? -1);
                    AppendInt32(hash, slot.OrderInCycle ?? -1);
                }
            }
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static string HashBytes(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static void AppendInstructionIdentity(IncrementalHash hash, IrInstruction instruction)
    {
        AppendInt32(hash, instruction.Index);
        AppendByte(hash, instruction.VirtualThreadId);
        AppendUInt64(hash, instruction.EncodedAddress);
        AppendInt32(hash, (int)instruction.Opcode);
        AppendInt32(hash, (int)instruction.DataType);
        AppendByte(hash, instruction.PredicateMask);
        AppendInt32(hash, instruction.Immediate);
        AppendUInt64(hash, instruction.StreamLength);
        AppendInt32(hash, instruction.Stride);
        AppendInt32(hash, instruction.RowStride);
        AppendBool(hash, instruction.Indexed);
        AppendBool(hash, instruction.Is2D);
        AppendBool(hash, instruction.Reduction);
        AppendBool(hash, instruction.TailAgnostic);
        AppendBool(hash, instruction.MaskAgnostic);
        AppendInt32(hash, (int)instruction.InstructionClass);
        AppendInt32(hash, (int)instruction.SerializationClass);
        AppendOperands(hash, instruction.Operands);
        AppendInt32(hash, (int)instruction.Annotation.ResourceClass);
        AppendInt32(hash, (int)instruction.Annotation.LatencyClass);
        AppendByte(hash, instruction.Annotation.MinimumLatencyCycles);
        AppendInt32(hash, (int)instruction.Annotation.StructurallyAllowedSlots);
        AppendInt32(hash, (int)instruction.Annotation.Serialization);
        AppendInt32(hash, (int)instruction.Annotation.StructuralResources);
        AppendInt32(hash, (int)instruction.Annotation.ControlFlowKind);
        AppendBool(hash, instruction.Annotation.IsBarrierLike);
        AppendBool(hash, instruction.Annotation.MayTrap);
        AppendNullableUInt64(hash, instruction.Annotation.EncodedBranchTarget);
        AppendNullableInt32(hash, instruction.Annotation.ResolvedBranchTargetInstructionIndex);
        AppendMemoryRegion(hash, instruction.Annotation.MemoryReadRegion);
        AppendMemoryRegion(hash, instruction.Annotation.MemoryWriteRegion);
        AppendOperands(hash, instruction.Annotation.Defs);
        AppendOperands(hash, instruction.Annotation.Uses);
        AppendInt32(hash, (int)instruction.Annotation.RequiredSlotClass);
        AppendInt32(hash, (int)instruction.Annotation.BindingKind);
        AppendUInt64(hash, instruction.Annotation.DomainTag);
        AppendBool(hash, instruction.Annotation.StealabilityHint);
        AppendString(hash, instruction.Annotation.BranchTargetSymbolName);
    }

    private static void AppendOperands(IncrementalHash hash, IReadOnlyList<IrOperand> operands)
    {
        AppendInt32(hash, operands.Count);
        foreach (IrOperand operand in operands)
        {
            AppendInt32(hash, (int)operand.Kind);
            AppendUInt64(hash, operand.Value);
            AppendString(hash, operand.Name);
        }
    }

    private static void AppendMemoryRegion(IncrementalHash hash, IrMemoryRegion? region)
    {
        AppendBool(hash, region is not null);
        if (region is null)
        {
            return;
        }

        AppendUInt64(hash, region.Address);
        AppendInt32(hash, checked((int)region.Length));
        AppendBool(hash, region.IsWrite);
    }

    private static void AppendInt32Sequence(IncrementalHash hash, IReadOnlyList<int> values)
    {
        AppendInt32(hash, values.Count);
        foreach (int value in values)
        {
            AppendInt32(hash, value);
        }
    }

    private static void AppendStringSequence(IncrementalHash hash, IReadOnlyList<string> values)
    {
        AppendInt32(hash, values.Count);
        foreach (string value in values.OrderBy(static value => value, StringComparer.Ordinal))
        {
            AppendString(hash, value);
        }
    }

    private static void AppendNullableInt32(IncrementalHash hash, int? value)
    {
        AppendBool(hash, value.HasValue);
        if (value.HasValue)
        {
            AppendInt32(hash, value.Value);
        }
    }

    private static void AppendNullableUInt64(IncrementalHash hash, ulong? value)
    {
        AppendBool(hash, value.HasValue);
        if (value.HasValue)
        {
            AppendUInt64(hash, value.Value);
        }
    }

    private static void AppendString(IncrementalHash hash, string? value)
    {
        if (value is null)
        {
            AppendInt32(hash, -1);
            return;
        }

        byte[] bytes = Encoding.UTF8.GetBytes(value);
        AppendInt32(hash, bytes.Length);
        hash.AppendData(bytes);
    }

    private static void AppendBool(IncrementalHash hash, bool value) =>
        AppendByte(hash, value ? (byte)1 : (byte)0);

    private static void AppendByte(IncrementalHash hash, byte value)
    {
        Span<byte> bytes = stackalloc byte[1];
        bytes[0] = value;
        hash.AppendData(bytes);
    }

    private static void AppendInt32(IncrementalHash hash, int value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        hash.AppendData(bytes);
    }

    private static void AppendUInt64(IncrementalHash hash, ulong value)
    {
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
        hash.AppendData(bytes);
    }
}

internal static class CompilerScheduleMetricsJsonV1
{
    internal static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };
}
