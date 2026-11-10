using System.Security.Cryptography;
using System.Text;
using HybridCPU.Compiler.Core.IR;

namespace HybridCPU.Compiler.Core.Target.Managed;

public enum HybridCpuManagedMetadataStatusV1 : byte
{
    Finalized = 0,
    Disabled = 1,
    Unsupported = 2,
    Unknown = 3,
    InvalidInput = 4,
    BudgetExhausted = 5
}

public sealed record HybridCpuManagedMetadataBudgetsV1(int MaximumSafepoints, int MaximumReferences)
{
    public static HybridCpuManagedMetadataBudgetsV1 Production { get; } = new(8192, 16384);
}

public sealed record HybridCpuManagedMetadataOptionsV1(
    bool EnableFinalization,
    bool EnableObjectReferences,
    HybridCpuManagedMetadataBudgetsV1 Budgets,
    string OptionsDigest)
{
    public static HybridCpuManagedMetadataOptionsV1 Production { get; } = Create(false, false,
        HybridCpuManagedMetadataBudgetsV1.Production);

    public static HybridCpuManagedMetadataOptionsV1 Qualification { get; } = Create(true, true,
        HybridCpuManagedMetadataBudgetsV1.Production);

    public static HybridCpuManagedMetadataOptionsV1 Create(
        bool enableFinalization,
        bool enableObjectReferences,
        HybridCpuManagedMetadataBudgetsV1 budgets)
    {
        ArgumentNullException.ThrowIfNull(budgets);
        string digest = HybridCpuManagedMetadataContractV1.Hash(string.Join('|',
            "hybridcpu.managed-metadata-options/v1", enableFinalization, enableObjectReferences,
            budgets.MaximumSafepoints, budgets.MaximumReferences));
        return new(enableFinalization, enableObjectReferences, budgets, digest);
    }
}

public sealed record HybridCpuManagedLiveReferenceRequestV1(
    string ValueIdentity,
    HybridCpuGcReferenceKindV1 ReferenceKind);

public sealed record HybridCpuManagedSafepointRequestV1(
    string InstructionIdentity,
    HybridCpuSafepointCategoryV1 Category,
    IReadOnlyList<HybridCpuManagedLiveReferenceRequestV1> LiveReferences);

public sealed record HybridCpuManagedMetadataRequestV1(
    string MethodIdentity,
    int CodeStartOffsetBytes,
    IrRegisterAllocationResultV1 Allocation,
    IReadOnlyList<HybridCpuManagedSafepointRequestV1> Safepoints,
    IReadOnlyList<HybridCpuManagedFixedRootRequestV1>? FixedRoots = null);

public sealed record HybridCpuManagedFixedRootRequestV1(
    string RootIdentity,
    string FrameSlotIdentity,
    HybridCpuGcReferenceKindV1 ReferenceKind);

public sealed record HybridCpuManagedMetadataArtifactV1(
    HybridCpuManagedMetadataStatusV1 Status,
    string Reason,
    IReadOnlyList<HybridCpuSafepointRecordV1> Safepoints,
    byte[] GcInfo,
    string? GcInfoDigest,
    byte[] CodeManagerMetadata,
    string? CodeManagerMetadataDigest,
    string? AllocationWitnessDigest,
    string ResultDigest);

public sealed class HybridCpuManagedMetadataContractV1
{
    public const string SchemaId = "hybridcpu.managed-metadata-finalization/v1";
    public const int EncodedBundleBytes = HybridCpuBundleSerializer.BundleSizeBytes;

    public static HybridCpuManagedMetadataContractV1 Default { get; } = new();

    private HybridCpuManagedMetadataContractV1()
    {
        ManagedAbiDigest = HybridCpuManagedAbiFamilyV1.Default.ContractDigest;
        AllocationContractDigest = HybridCpuRegisterAllocationContractV1.Default.ContractDigest;
        ProductionOptionsDigest = HybridCpuManagedMetadataOptionsV1.Production.OptionsDigest;
        QualificationOptionsDigest = HybridCpuManagedMetadataOptionsV1.Qualification.OptionsDigest;
        BasicBlockOnlySelectedPlansDigest = HybridCpuRegisterAllocationContractV1.Hash(
            "selected-plans/v1||||");
        ContractDigest = Hash(string.Join('|', SchemaId, ManagedAbiDigest, AllocationContractDigest,
            ProductionOptionsDigest, QualificationOptionsDigest, BasicBlockOnlySelectedPlansDigest,
            "final-allocation-only", "exact-w8-offsets",
            "object-reference-only", "fixed-frame-roots-explicit", "byref=unsupported", "interior=unsupported", "runtime-authority=false"));
    }

    public string ManagedAbiDigest { get; }
    public string AllocationContractDigest { get; }
    public string ProductionOptionsDigest { get; }
    public string QualificationOptionsDigest { get; }
    public string BasicBlockOnlySelectedPlansDigest { get; }
    public string ContractDigest { get; }
    public string Phase05SafepointPolicy => "all-final-call-sites;allocation-via-runtime-call;phase11-polls=explicit-managed-helper-sites";

    internal static string Hash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
}

/// <summary>
/// Produces descriptive GC/code-manager metadata from the final allocation and W=8 placement.
/// It has no collection, suspension, stack-walk, execution, publication, commit or retire authority.
/// </summary>
public sealed class HybridCpuManagedMetadataFinalizerV1
{
    /// <summary>
    /// Derives the complete Phase 05 safepoint set from the final allocated representation.
    /// Every call is a safepoint; allocation slow paths are calls. Loop polls are deliberately
    /// absent because V1 collection is synchronous and single-context, not concurrent/requested.
    /// </summary>
    public HybridCpuManagedMetadataArtifactV1 FinalizeRequiredCallSites(
        string methodIdentity,
        int codeStartOffsetBytes,
        IrRegisterAllocationResultV1 allocation,
        HybridCpuManagedMetadataOptionsV1? options = null,
        IReadOnlyList<HybridCpuManagedFixedRootRequestV1>? fixedRoots = null)
        => FinalizeRequiredCallSites(CancellationToken.None, methodIdentity, codeStartOffsetBytes, allocation, options, fixedRoots);

    public HybridCpuManagedMetadataArtifactV1 FinalizeRequiredCallSites(
        CancellationToken cancellationToken,
        string methodIdentity,
        int codeStartOffsetBytes,
        IrRegisterAllocationResultV1 allocation,
        HybridCpuManagedMetadataOptionsV1? options = null,
        IReadOnlyList<HybridCpuManagedFixedRootRequestV1>? fixedRoots = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (allocation is null || allocation.Witness is null || allocation.OriginalSchedule is null ||
            allocation.FinalBundles is null)
            return Failure(HybridCpuManagedMetadataStatusV1.Unknown,
                "A final allocation is required to derive mandatory call safepoints.");
        Dictionary<string, (int CodeOffset, int ScheduledPosition, int InstructionIndex)> sites =
            BuildSites(cancellationToken, allocation.OriginalSchedule, allocation.FinalBundles);
        Dictionary<int, HashSet<string>> liveBefore = BuildLiveValuesBeforeInstruction(cancellationToken, allocation.OriginalSchedule);
        HashSet<string> managedValues = allocation.Witness.SemanticValues.Where(static value =>
                value.VirtualClass == IrVirtualValueClass.ManagedObjectReference &&
                value.ValueKind.Kind == IrCanonicalValueKind.ManagedObjectReference)
            .Select(static value => value.ValueId).ToHashSet(StringComparer.Ordinal);
        HybridCpuManagedSafepointRequestV1[] safepoints = allocation.OriginalSchedule.Program.Instructions
            .Where(static instruction => instruction.Annotation.IsManagedGcSafepoint &&
                (instruction.Annotation.ControlFlowKind == IrControlFlowKind.Call ||
                 instruction.SideEffects.ArchitecturalEffects.HasFlag(IrArchitecturalEffectKind.Call)))
            .Select(instruction => new HybridCpuManagedSafepointRequestV1(
                instruction.StableIdentity,
                HybridCpuSafepointCategoryV1.CallSite,
                managedValues.Where(identity => liveBefore.TryGetValue(instruction.Index, out HashSet<string>? live) &&
                        live.Contains(identity))
                    .Order(StringComparer.Ordinal)
                    .Select(static identity => new HybridCpuManagedLiveReferenceRequestV1(
                        identity, HybridCpuGcReferenceKindV1.ObjectReference)).ToArray()))
            .OrderBy(point => sites.TryGetValue(point.InstructionIdentity, out (int CodeOffset, int ScheduledPosition, int InstructionIndex) site) ? site.CodeOffset : int.MaxValue)
            .ToArray();
        return Finalize(cancellationToken, new(methodIdentity, codeStartOffsetBytes, allocation, safepoints, fixedRoots), options);
    }

    public HybridCpuManagedMetadataArtifactV1 Finalize(
        HybridCpuManagedMetadataRequestV1 request,
        HybridCpuManagedMetadataOptionsV1? options = null)
        => Finalize(CancellationToken.None, request, options);

    public HybridCpuManagedMetadataArtifactV1 Finalize(
        CancellationToken cancellationToken,
        HybridCpuManagedMetadataRequestV1 request,
        HybridCpuManagedMetadataOptionsV1? options = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        options ??= HybridCpuManagedMetadataOptionsV1.Production;
        if (!ValidOptions(options)) return Failure(HybridCpuManagedMetadataStatusV1.InvalidInput,
            "Managed metadata options do not bind valid deterministic budgets.");
        if (!options.EnableFinalization) return Failure(HybridCpuManagedMetadataStatusV1.Disabled,
            "Production managed metadata finalization is disabled.");
        if (string.IsNullOrWhiteSpace(request.MethodIdentity) || request.CodeStartOffsetBytes < 0 ||
            request.CodeStartOffsetBytes % HybridCpuManagedMetadataContractV1.EncodedBundleBytes != 0 ||
            request.Safepoints is null)
            return Failure(HybridCpuManagedMetadataStatusV1.InvalidInput,
                "Method identity, aligned code start and safepoint collection are required.");
        IReadOnlyList<HybridCpuManagedFixedRootRequestV1> fixedRoots = request.FixedRoots ?? [];
        long liveReferenceCount = request.Safepoints.Sum(
            static point => (long)(point?.LiveReferences?.Count ?? 0));
        long fixedReferenceCount = (long)request.Safepoints.Count * fixedRoots.Count;
        long totalReferenceCount = liveReferenceCount + fixedReferenceCount;
        if (request.Safepoints.Count > options.Budgets.MaximumSafepoints ||
            totalReferenceCount > options.Budgets.MaximumReferences)
            return Failure(HybridCpuManagedMetadataStatusV1.BudgetExhausted,
                $"Managed metadata deterministic budgets were exceeded: safepoints={request.Safepoints.Count}/" +
                $"{options.Budgets.MaximumSafepoints}, references={totalReferenceCount}/" +
                $"{options.Budgets.MaximumReferences} (live={liveReferenceCount}, fixed={fixedReferenceCount}).");

        IrRegisterAllocationResultV1 allocation = request.Allocation;
        if (allocation is null || allocation.Status != IrRegisterAllocationStatusV1.Allocated || allocation.Witness is null)
            return Failure(HybridCpuManagedMetadataStatusV1.Unknown,
                "A successful final Phase 20 allocation witness is required.");
        IrRegisterAllocationWitnessV1 witness = allocation.Witness;
        if (!ReferenceEquals(allocation.FinalBundles.ProgramSchedule, allocation.FinalSchedule) ||
            !witness.Rebuild.DependenciesCurrent || !witness.Rebuild.LivenessCurrent ||
            !witness.Rebuild.PressureCurrent || !witness.Rebuild.ResourceFactsCurrent ||
            !witness.Rebuild.ExactW8PlacementRecomputed ||
            !string.Equals(witness.ContractDigest, HybridCpuRegisterAllocationContractV1.Default.ContractDigest, StringComparison.Ordinal) ||
            !string.Equals(witness.SelectedPlansDigest,
                HybridCpuManagedMetadataContractV1.Default.BasicBlockOnlySelectedPlansDigest, StringComparison.Ordinal) ||
            !VerifyFinalScheduleAndPlacement(allocation) ||
            !VerifyWitnessDigest(witness))
            return Failure(HybridCpuManagedMetadataStatusV1.Unknown,
                "Allocation, liveness, frame or exact W=8 placement evidence is stale or malformed.");

        if (!options.EnableObjectReferences && (request.Safepoints.Any(static point => point?.LiveReferences?.Count != 0) || fixedRoots.Count != 0))
            return Failure(HybridCpuManagedMetadataStatusV1.Unsupported,
                "Object-reference metadata is not enabled by these options.");

        Dictionary<string, (int CodeOffset, int ScheduledPosition, int InstructionIndex)> sites = BuildSites(
            cancellationToken, allocation.OriginalSchedule, allocation.FinalBundles);
        Dictionary<string, IrRegisterAssignmentV1> assignments = witness.Assignments
            .ToDictionary(static item => item.ValueId, StringComparer.Ordinal);
        Dictionary<string, IrVirtualValueV1> originalValues = allocation.OriginalSchedule.Program.ValueFlow.Values
            .ToDictionary(static value => value.StableId, StringComparer.Ordinal);
        if (witness.SemanticValues.Any(value => !originalValues.TryGetValue(value.ValueId, out IrVirtualValueV1? original) ||
                original.ValueKind != value.ValueKind || original.VirtualClass != value.VirtualClass) ||
            !witness.SemanticValues.Select(static value => value.ValueId).Order(StringComparer.Ordinal).SequenceEqual(
                assignments.Keys.Concat(witness.Spills.Select(static spill => spill.ValueId)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal),
                StringComparer.Ordinal))
            return Failure(HybridCpuManagedMetadataStatusV1.Unknown,
                "Allocation witness semantic value kinds are stale or incomplete.");
        Dictionary<string, IrVirtualValueV1> managedValues = witness.SemanticValues
            .Where(static value => value.VirtualClass == IrVirtualValueClass.ManagedObjectReference &&
                value.ValueKind.Kind == IrCanonicalValueKind.ManagedObjectReference)
            .Select(value => originalValues[value.ValueId])
            .Where(static value => value.Allocation.LegalContours.Contains("managed-object-reference", StringComparer.Ordinal))
            .ToDictionary(static value => value.StableId, StringComparer.Ordinal);
        Dictionary<int, HashSet<string>> liveBefore = BuildLiveValuesBeforeInstruction(cancellationToken, allocation.OriginalSchedule);
        Dictionary<string, IrSpillDecisionV1> spills = witness.Spills
            .ToDictionary(static item => item.ValueId, StringComparer.Ordinal);
        Dictionary<string, HybridCpuFrameSlotV2> frameSlots = witness.Frame.Slots
            .ToDictionary(static item => item.Identity, StringComparer.Ordinal);
        if (fixedRoots.Any(static root => root is null || string.IsNullOrWhiteSpace(root.RootIdentity) ||
                string.IsNullOrWhiteSpace(root.FrameSlotIdentity) || root.ReferenceKind != HybridCpuGcReferenceKindV1.ObjectReference) ||
            fixedRoots.Select(static root => root.RootIdentity).Distinct(StringComparer.Ordinal).Count() != fixedRoots.Count ||
            fixedRoots.Select(static root => root.FrameSlotIdentity).Distinct(StringComparer.Ordinal).Count() != fixedRoots.Count ||
            fixedRoots.Any(root => !frameSlots.TryGetValue(root.FrameSlotIdentity, out HybridCpuFrameSlotV2? slot) ||
                !root.FrameSlotIdentity.StartsWith("eh-home:", StringComparison.Ordinal) || slot.SizeBytes != 8 ||
                slot.AlignmentBytes < 8 || slot.OffsetFromAdjustedStackPointerBytes < 0 ||
                slot.OffsetFromAdjustedStackPointerBytes + 8 > witness.Frame.FrameSizeBytes))
            return Failure(HybridCpuManagedMetadataStatusV1.InvalidInput,
                "Fixed managed roots require unique exact EH-home word slots in the final frame.");
        List<HybridCpuSafepointRecordV1> records = new List<HybridCpuSafepointRecordV1>(request.Safepoints.Count);
        HashSet<string> identities = new HashSet<string>(StringComparer.Ordinal);

        foreach (HybridCpuManagedSafepointRequestV1 point in request.Safepoints)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (point is null || string.IsNullOrWhiteSpace(point.InstructionIdentity) || point.LiveReferences is null ||
                !identities.Add(point.InstructionIdentity))
                return Failure(HybridCpuManagedMetadataStatusV1.InvalidInput,
                    "Safepoint identities must be present and unique.");
            if (!sites.TryGetValue(point.InstructionIdentity,
                    out (int CodeOffset, int ScheduledPosition, int InstructionIndex) site))
                return Failure(HybridCpuManagedMetadataStatusV1.Unknown,
                    $"Safepoint '{point.InstructionIdentity}' is absent from final placement.");
            if (point.LiveReferences.Select(static item => item.ValueIdentity)
                .Distinct(StringComparer.Ordinal).Count() != point.LiveReferences.Count)
                return Failure(HybridCpuManagedMetadataStatusV1.InvalidInput,
                    "Live reference identities must be unique at each safepoint.");
            string[] requiredReferences = managedValues.Keys.Where(identity =>
                    liveBefore.TryGetValue(site.InstructionIndex, out HashSet<string>? live) && live.Contains(identity))
                .Order(StringComparer.Ordinal).ToArray();
            string[] requestedReferences = point.LiveReferences.Select(static item => item.ValueIdentity)
                .Order(StringComparer.Ordinal).ToArray();
            if (!requiredReferences.SequenceEqual(requestedReferences, StringComparer.Ordinal))
                return Failure(HybridCpuManagedMetadataStatusV1.Unsupported,
                    "Safepoint root coverage does not exactly match final managed-reference liveness.");

            List<HybridCpuGcReferenceLocationV1> locations = new List<HybridCpuGcReferenceLocationV1>(point.LiveReferences.Count);
            foreach (HybridCpuManagedLiveReferenceRequestV1 reference in point.LiveReferences)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (reference is null || string.IsNullOrWhiteSpace(reference.ValueIdentity))
                    return Failure(HybridCpuManagedMetadataStatusV1.InvalidInput,
                        "Live reference identities must be explicit.");
                if (reference.ReferenceKind != HybridCpuGcReferenceKindV1.ObjectReference)
                    return Failure(HybridCpuManagedMetadataStatusV1.Unsupported,
                        "Managed byrefs and interior references are not qualified.");
                if (!managedValues.ContainsKey(reference.ValueIdentity))
                    return Failure(HybridCpuManagedMetadataStatusV1.Unsupported,
                        $"Value '{reference.ValueIdentity}' is not typed as a managed object reference.");
                if (assignments.TryGetValue(reference.ValueIdentity, out IrRegisterAssignmentV1? assignment))
                {
                    if (site.ScheduledPosition < assignment.ScheduledStart || site.ScheduledPosition >= assignment.ScheduledEndExclusive)
                        return Failure(HybridCpuManagedMetadataStatusV1.Unknown,
                            $"Reference '{reference.ValueIdentity}' is not live at the final safepoint.");
                    locations.Add(new(reference.ValueIdentity, reference.ReferenceKind,
                        HybridCpuGcLocationKindV1.Register, assignment.RegisterId, null));
                    continue;
                }
                if (spills.TryGetValue(reference.ValueIdentity, out IrSpillDecisionV1? spill) &&
                    frameSlots.TryGetValue(spill.FrameSlotIdentity, out HybridCpuFrameSlotV2? slot))
                {
                    // A reload copies the current SSA reference out of its owned frame
                    // slot; it does not invalidate the slot. A defining store precedes
                    // the value's live interval. Therefore the exact spill slot remains
                    // a valid root throughout every liveness-qualified safepoint. Do not
                    // compare original spill-access indices with final mutated indices.
                    locations.Add(new(reference.ValueIdentity, reference.ReferenceKind,
                        HybridCpuGcLocationKindV1.Stack, null, slot.OffsetFromAdjustedStackPointerBytes));
                    continue;
                }
                return Failure(HybridCpuManagedMetadataStatusV1.Unknown,
                    $"Reference '{reference.ValueIdentity}' has no exact final register or stable spill-slot location.");
            }
            foreach (HybridCpuManagedFixedRootRequestV1 root in fixedRoots)
            {
                cancellationToken.ThrowIfCancellationRequested();
                HybridCpuFrameSlotV2 slot = frameSlots[root.FrameSlotIdentity];
                locations.Add(new(root.RootIdentity, root.ReferenceKind,
                    HybridCpuGcLocationKindV1.Stack, null, slot.OffsetFromAdjustedStackPointerBytes));
            }
            records.Add(new(site.CodeOffset, point.Category, locations
                .OrderBy(static item => item.ValueIdentity, StringComparer.Ordinal).ToArray()));
        }

        cancellationToken.ThrowIfCancellationRequested();
        HybridCpuGcInfoEncodingResultV1 gc = HybridCpuManagedAbiEncodingV1.EncodeGcInfo(records);
        if (gc.Status != HybridCpuPlatformFactStatus.Supported)
            return Failure(gc.Status == HybridCpuPlatformFactStatus.Unsupported
                    ? HybridCpuManagedMetadataStatusV1.Unsupported : HybridCpuManagedMetadataStatusV1.InvalidInput,
                gc.Reason);
        int codeSize = checked(allocation.FinalBundles.BlockResults.Sum(static block => block.Bundles.Count) *
            HybridCpuManagedMetadataContractV1.EncodedBundleBytes);
        byte[] codeManager = HybridCpuManagedAbiEncodingV1.EncodeCodeManagerMetadata(
            [new(request.MethodIdentity, request.CodeStartOffsetBytes, codeSize, gc.Digest, null)]);
        string codeManagerDigest = HybridCpuManagedMetadataContractV1.Hash(Convert.ToHexString(codeManager));
        string resultDigest = HybridCpuManagedMetadataContractV1.Hash(string.Join('|',
            HybridCpuManagedMetadataContractV1.Default.ContractDigest, options.OptionsDigest,
            witness.WitnessDigest, gc.Digest, codeManagerDigest));
        cancellationToken.ThrowIfCancellationRequested();
        return new(HybridCpuManagedMetadataStatusV1.Finalized,
            "Final-allocation object-reference and safepoint metadata was finalized deterministically.",
            records.OrderBy(static item => item.CodeOffsetBytes).ToArray(), gc.Bytes, gc.Digest,
            codeManager, codeManagerDigest, witness.WitnessDigest, resultDigest);
    }

    private static Dictionary<string, (int CodeOffset, int ScheduledPosition, int InstructionIndex)> BuildSites(
        CancellationToken cancellationToken,
        IrProgramSchedule originalSchedule,
        IrProgramBundlingResult bundles)
    {
        Dictionary<int, int> positions = BuildScheduledPositions(cancellationToken, originalSchedule, out _);
        Dictionary<string, int> originalPositions = originalSchedule.Program.Instructions.ToDictionary(
            static instruction => instruction.StableIdentity,
            instruction => positions[instruction.Index],
            StringComparer.Ordinal);
        Dictionary<string, int> originalInstructionIndices = originalSchedule.Program.Instructions.ToDictionary(
            static instruction => instruction.StableIdentity,
            static instruction => instruction.Index,
            StringComparer.Ordinal);
        Dictionary<string, (int, int, int)> result = new Dictionary<string, (int, int, int)>(StringComparer.Ordinal);
        int bundleIndex = 0;
        foreach (IrBasicBlockBundlingResult block in bundles.BlockResults.OrderBy(static item => item.Block.StartInstructionIndex))
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (IrMaterializedBundle bundle in block.Bundles.OrderBy(static item => item.Cycle))
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (IrInstruction instruction in bundle.Slots.Where(static slot => slot.Instruction is not null)
                    .OrderBy(static slot => slot.SlotIndex).Select(static slot => slot.Instruction!))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (originalPositions.TryGetValue(instruction.StableIdentity, out int originalPosition))
                        result[instruction.StableIdentity] = (checked(bundleIndex *
                            HybridCpuManagedMetadataContractV1.EncodedBundleBytes), originalPosition,
                            originalInstructionIndices[instruction.StableIdentity]);
                }
                bundleIndex++;
            }
        }
        return result;
    }

    private static Dictionary<int, int> BuildScheduledPositions(IrProgramSchedule schedule)
    {
        return BuildScheduledPositions(CancellationToken.None, schedule, out _);
    }

    private static Dictionary<int, int> BuildScheduledPositions(
        IrProgramSchedule schedule,
        out Dictionary<int, (int Start, int End)> blockPositions)
        => BuildScheduledPositions(CancellationToken.None, schedule, out blockPositions);

    private static Dictionary<int, int> BuildScheduledPositions(
        CancellationToken cancellationToken,
        IrProgramSchedule schedule,
        out Dictionary<int, (int Start, int End)> blockPositions)
    {
        Dictionary<int, int> positions = new Dictionary<int, int>();
        blockPositions = [];
        int blockBase = 0;
        foreach (IrBasicBlockSchedule block in schedule.BlockSchedules.OrderBy(static item => item.Block.StartInstructionIndex))
        {
            cancellationToken.ThrowIfCancellationRequested();
            int start = checked(blockBase * 2);
            foreach (IrScheduledInstruction instruction in block.ScheduledInstructions)
                positions[instruction.InstructionIndex] = checked((blockBase + instruction.Cycle * 8 + instruction.OrderInCycle) * 2);
            int end = checked((blockBase + Math.Max(1, block.ScheduleLength) * 8) * 2);
            blockPositions[block.BlockId] = (start, end);
            blockBase = checked(end / 2 + 8);
        }
        return positions;
    }

    private static Dictionary<string, (int Start, int End)> BuildValueIntervals(IrProgramSchedule schedule)
    {
        Dictionary<int, int> positions = BuildScheduledPositions(schedule,
            out Dictionary<int, (int Start, int End)> blockPositions);
        Dictionary<string, List<IrValueAccessV1>> accesses = schedule.Program.ValueFlow.Accesses
            .GroupBy(static access => access.ValueId, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.ToList(), StringComparer.Ordinal);
        Dictionary<int, IrBlockLivenessV1> liveness = schedule.ValueAnalysis.Blocks
            .ToDictionary(static block => block.BlockId);
        Dictionary<string, (int Start, int End)> intervals = new Dictionary<string, (int Start, int End)>(StringComparer.Ordinal);
        foreach (IrVirtualValueV1 value in schedule.Program.ValueFlow.Values)
        {
            if (!accesses.TryGetValue(value.StableId, out List<IrValueAccessV1>? valueAccesses) || valueAccesses.Count == 0)
                continue;
            bool methodIngress = value.StableId.EndsWith(":abi", StringComparison.Ordinal) &&
                value.Allocation.FixedRegisterId is int ingressRegister && valueAccesses.Count == 1 &&
                valueAccesses[0].Kind == IrValueAccessKind.Use &&
                schedule.Program.Instructions.SingleOrDefault(instruction =>
                    instruction.Index == valueAccesses[0].InstructionIndex) is IrInstruction ingressCopy &&
                ingressCopy.StableIdentity.EndsWith(":argument-copy", StringComparison.Ordinal) &&
                ingressCopy.Annotation.Uses.Any(operand => operand.Kind == IrOperandKind.ArchitecturalRegister &&
                    operand.Value == checked((ulong)ingressRegister));
            bool callArgumentBridge = value.StableId.EndsWith("-arg-abi:value", StringComparison.Ordinal) &&
                valueAccesses.Count == 2 && valueAccesses.SingleOrDefault(static access =>
                    access.Kind == IrValueAccessKind.Def) is IrValueAccessV1 argumentDefinition &&
                valueAccesses.SingleOrDefault(static access =>
                    access.Kind == IrValueAccessKind.Use) is IrValueAccessV1 argumentUse &&
                schedule.Program.Instructions.Single(instruction => instruction.Index == argumentDefinition.InstructionIndex)
                    .StableIdentity.EndsWith("-arg-copy", StringComparison.Ordinal) &&
                IsCall(schedule.Program.Instructions.Single(instruction => instruction.Index == argumentUse.InstructionIndex));
            bool callResultBridge = (value.StableId.EndsWith("-result-abi:value", StringComparison.Ordinal) ||
                    value.StableId.Contains(":call-result-abi:value", StringComparison.Ordinal)) &&
                valueAccesses.Count == 2 && valueAccesses.SingleOrDefault(static access =>
                    access.Kind == IrValueAccessKind.Def) is IrValueAccessV1 resultDefinition &&
                valueAccesses.SingleOrDefault(static access =>
                    access.Kind == IrValueAccessKind.Use) is IrValueAccessV1 resultUse &&
                IsCall(schedule.Program.Instructions.Single(instruction => instruction.Index == resultDefinition.InstructionIndex)) &&
                schedule.Program.Instructions.Single(instruction => instruction.Index == resultUse.InstructionIndex)
                    .StableIdentity is string resultCopyIdentity &&
                (resultCopyIdentity.EndsWith("-result-copy", StringComparison.Ordinal) ||
                 resultCopyIdentity.EndsWith(":result-copy", StringComparison.Ordinal));
            bool exactAbiBridge = methodIngress || callArgumentBridge || callResultBridge;
            int start = valueAccesses.Min(access => checked(positions[access.InstructionIndex] +
                (access.Kind == IrValueAccessKind.Def ? 1 : 0)));
            int end = checked(valueAccesses.Max(access => checked(positions[access.InstructionIndex] +
                (access.Kind == IrValueAccessKind.Def ? 1 : 0))) + 1);
            foreach ((int blockId, IrBlockLivenessV1 block) in liveness)
            {
                if (exactAbiBridge) break;
                if (block.LiveIn.Contains(value.StableId, StringComparer.Ordinal))
                    start = Math.Min(start, blockPositions[blockId].Start);
                if (block.LiveOut.Contains(value.StableId, StringComparer.Ordinal))
                    end = Math.Max(end, blockPositions[blockId].End);
            }
            intervals[value.StableId] = (start, end);
        }
        return intervals;
    }

    private static Dictionary<int, HashSet<string>> BuildLiveValuesBeforeInstruction(CancellationToken cancellationToken, IrProgramSchedule schedule)
    {
        Dictionary<int, IrBlockLivenessV1> blockLiveness = schedule.ValueAnalysis.Blocks
            .ToDictionary(static block => block.BlockId);
        Dictionary<int, IrValueAccessV1[]> accesses = schedule.Program.ValueFlow.Accesses
            .Where(static access => access.Kind != IrValueAccessKind.PhiEdgeUse)
            .GroupBy(static access => access.InstructionIndex)
            .ToDictionary(static group => group.Key, static group => group.ToArray());
        Dictionary<int, HashSet<string>> result = new Dictionary<int, HashSet<string>>();
        foreach (IrBasicBlockSchedule blockSchedule in schedule.BlockSchedules)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!blockLiveness.TryGetValue(blockSchedule.BlockId, out IrBlockLivenessV1? block))
                throw new InvalidOperationException($"Missing liveness for basic block {blockSchedule.BlockId}.");
            HashSet<string> live = new HashSet<string>(block.LiveOut, StringComparer.Ordinal);
            foreach (IrInstruction instruction in blockSchedule.Block.Instructions.OrderByDescending(static item => item.Index))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (accesses.TryGetValue(instruction.Index, out IrValueAccessV1[]? instructionAccesses))
                {
                    foreach (IrValueAccessV1 definition in instructionAccesses.Where(static access =>
                                 access.Kind == IrValueAccessKind.Def))
                        live.Remove(definition.ValueId);
                    foreach (IrValueAccessV1 use in instructionAccesses.Where(static access =>
                                 access.Kind == IrValueAccessKind.Use))
                        live.Add(use.ValueId);
                }
                result[instruction.Index] = new HashSet<string>(live, StringComparer.Ordinal);
            }
        }
        return result;
    }

    private static bool IsCall(IrInstruction instruction) =>
        instruction.Annotation.ControlFlowKind == IrControlFlowKind.Call ||
        instruction.SideEffects.ArchitecturalEffects.HasFlag(IrArchitecturalEffectKind.Call);

    private static bool VerifyWitnessDigest(IrRegisterAllocationWitnessV1 witness)
    {
        string digest = HybridCpuRegisterAllocationContractV1.Hash(string.Join('|',
            HybridCpuRegisterAllocationContractV1.SchemaId,
            HybridCpuRegisterAllocationContractV1.Default.ContractDigest,
            witness.OptionsDigest, witness.ResourceModelDigest, witness.InputScheduleDigest, witness.SelectedPlansDigest,
            string.Join(';', witness.Assignments.Select(static assignment =>
                $"{assignment.ValueId}:{assignment.RegisterId}:{assignment.ScheduledStart}:{assignment.ScheduledEndExclusive}:{assignment.LiveAcrossCall}")),
            string.Join(';', witness.Spills.Select(spill => $"{spill.ValueId}:{spill.FrameSlotIdentity}:{spill.SpillCost}:" +
                string.Join(',', spill.Accesses.Select(static access => $"{access.OriginalInstructionIndex}:{access.AccessKind}:{access.ScratchRegisterId}")))),
            string.Join(';', witness.SemanticValues.Select(static value =>
                $"{value.ValueId}:{value.ValueKind.Kind}:{value.ValueKind.BitWidth}:{value.ValueKind.IsSigned}:{value.ValueKind.LaneCount}:{value.VirtualClass}")),
            witness.Frame.Digest,
            string.Join(';', witness.Mutations.Select(static mutation =>
                $"{mutation.Kind}:{mutation.Identity}:{mutation.FinalInstructionIndex}")),
            witness.Rebuild.DependencyDigest, witness.Rebuild.ValueFlowDigest, witness.Rebuild.ScheduleDigest,
            witness.Rebuild.PlacementDigest,
            string.Join(';', witness.Rebuild.Loops.Select(static loop =>
                $"{loop.LoopId}:{loop.CanonicalStatus}:{loop.MiiEligibility}:{loop.ProvenLowerBoundIi}:{loop.DistanceDagDigest}:{loop.MiiProofDigest}"))));
        return string.Equals(digest, witness.WitnessDigest, StringComparison.Ordinal);
    }

    private static bool VerifyFinalScheduleAndPlacement(IrRegisterAllocationResultV1 allocation)
    {
        IrRegisterAllocationWitnessV1 witness = allocation.Witness!;
        string finalSchedule = DigestScheduleOnly(allocation.FinalSchedule);
        string finalPlacement = DigestPlacement(allocation.FinalBundles);
        string inputBasis = $"{DigestScheduleOnly(allocation.OriginalSchedule)}|{DigestPlacement(allocation.OriginalBundles)}";
        IrInstruction[] fixedAccesses = allocation.OriginalSchedule.Program.Instructions
            .Where(static instruction => instruction.Annotation.FixedFrameSlotIdentity is not null)
            .OrderBy(static instruction => instruction.Index).ToArray();
        if (fixedAccesses.Length != 0)
            inputBasis += "|fixed-frame-access/v1|" + string.Join(';', fixedAccesses.Select(static instruction =>
                $"{instruction.Index}:{instruction.StableIdentity}:{instruction.Annotation.FixedFrameSlotIdentity}"));
        string inputSchedule = HybridCpuRegisterAllocationContractV1.Hash(inputBasis);
        return string.Equals(finalSchedule, witness.Rebuild.ScheduleDigest, StringComparison.Ordinal) &&
            string.Equals(finalPlacement, witness.Rebuild.PlacementDigest, StringComparison.Ordinal) &&
            string.Equals(inputSchedule, witness.InputScheduleDigest, StringComparison.Ordinal);
    }

    private static string DigestScheduleOnly(IrProgramSchedule schedule) =>
        HybridCpuRegisterAllocationContractV1.Hash(string.Join('|', "schedule/v1",
            schedule.Program.Contract.DerivedFacts.ProgramMutation.Value,
            string.Join(';', schedule.BlockSchedules.OrderBy(static block => block.BlockId)
                .SelectMany(block => block.ScheduledInstructions.OrderBy(static instruction => instruction.InstructionIndex)
                    .Select(instruction => $"{block.BlockId}:{instruction.InstructionIndex}:{instruction.Cycle}:{instruction.OrderInCycle}")))));

    private static string DigestPlacement(IrProgramBundlingResult bundles) =>
        HybridCpuRegisterAllocationContractV1.Hash(string.Join('|', "placement/v1",
            string.Join(';', bundles.BlockResults.OrderBy(static block => block.BlockId)
                .SelectMany(block => block.Bundles.OrderBy(static bundle => bundle.Cycle)
                    .Select(bundle => $"{block.BlockId}:{bundle.Cycle}:{string.Join(',', bundle.Slots.Select(slot => slot.Instruction?.Index ?? -1))}")))));

    private static bool ValidOptions(HybridCpuManagedMetadataOptionsV1 options)
    {
        HybridCpuManagedMetadataBudgetsV1 budgets = options.Budgets;
        if (budgets is null || budgets.MaximumSafepoints <= 0 || budgets.MaximumReferences <= 0 ||
            budgets.MaximumSafepoints > HybridCpuManagedAbiFamilyV1.MaximumSafepoints ||
            budgets.MaximumReferences > HybridCpuManagedAbiFamilyV1.MaximumSafepoints *
                HybridCpuManagedAbiFamilyV1.MaximumReferencesPerSafepoint)
            return false;
        return string.Equals(options.OptionsDigest,
            HybridCpuManagedMetadataOptionsV1.Create(options.EnableFinalization,
                options.EnableObjectReferences, budgets).OptionsDigest, StringComparison.Ordinal);
    }

    private static HybridCpuManagedMetadataArtifactV1 Failure(
        HybridCpuManagedMetadataStatusV1 status,
        string reason) => new(status, reason, Array.Empty<HybridCpuSafepointRecordV1>(),
            Array.Empty<byte>(), null, Array.Empty<byte>(), null, null,
            HybridCpuManagedMetadataContractV1.Hash($"failure|{status}|{reason}"));
}
