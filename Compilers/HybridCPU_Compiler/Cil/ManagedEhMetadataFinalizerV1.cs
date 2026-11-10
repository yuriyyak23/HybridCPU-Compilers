using System.Buffers.Binary;
using System.Globalization;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.Target;
using HybridCPU.Compiler.Core.Target.Managed;
using HybridCPU.Compiler.Core.Target.Object;
using HybridCPU.Platform.Contracts;

namespace HybridCPU.Compiler.Cil;

public sealed class ManagedEhMetadataFinalizerV1
{
    public ManagedEhFinalizationArtifactV1 Finalize(ManagedEhMethodPlanV1 plan, IrRegisterAllocationResultV1 allocation)
        => Finalize(CancellationToken.None, plan, allocation);

    /// <summary>Cooperative EH finalization checkpoints; encoding/hash internals retain their existing ownership.</summary>
    public ManagedEhFinalizationArtifactV1 Finalize(CancellationToken cancellationToken,
        ManagedEhMethodPlanV1 plan, IrRegisterAllocationResultV1 allocation)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(allocation);
        cancellationToken.ThrowIfCancellationRequested();
        if (!ValidPlan(plan))
            return Failure("The managed EH plan is malformed, stale or not bound to its exact deterministic digest.");
        cancellationToken.ThrowIfCancellationRequested();
        if (allocation.Status != IrRegisterAllocationStatusV1.Allocated || allocation.Witness is null ||
            allocation.FinalBundles is null || allocation.OriginalSchedule is null ||
            !allocation.Witness.Rebuild.DependenciesCurrent || !allocation.Witness.Rebuild.LivenessCurrent ||
            !allocation.Witness.Rebuild.PressureCurrent || !allocation.Witness.Rebuild.ResourceFactsCurrent ||
            !allocation.Witness.Rebuild.ExactW8PlacementRecomputed)
            return Failure("Final post-scheduling, post-RA and post-frame-lowering evidence is required.");

        SortedDictionary<int, int> placement = FinalIlPlacement(cancellationToken, allocation.FinalBundles, plan.InstructionIdentityPrefix);
        cancellationToken.ThrowIfCancellationRequested();
        int codeSize = checked(allocation.FinalBundles.BlockResults.Sum(static row => row.Bundles.Count) *
            HybridCpuBundleSerializer.BundleSizeBytes);
        if (placement.Count == 0 || string.IsNullOrWhiteSpace(plan.MethodIdentity) ||
            string.IsNullOrWhiteSpace(plan.InstructionIdentityPrefix) ||
            !allocation.OriginalSchedule.Program.Instructions.Any(instruction =>
                instruction.StableIdentity.StartsWith(plan.InstructionIdentityPrefix, StringComparison.Ordinal)))
            return Failure("Final placement does not contain exact CIL instruction identities.");
        int Map(int ilOffset)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return placement.Where(row => row.Key >= ilOffset).Select(static row => row.Value)
                .DefaultIfEmpty(codeSize).Min();
        }

        List<HybridCpuManagedEhClauseRegistrationV1> clauses = new List<HybridCpuManagedEhClauseRegistrationV1>(plan.Clauses.Count);
        foreach (ManagedEhClausePlanV1 clause in plan.Clauses.OrderBy(static row => row.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            int tryStart = Map(clause.TryOffset);
            int tryEnd = Map(checked(clause.TryOffset + clause.TryLength));
            int handlerStart = Map(clause.HandlerOffset);
            int handlerEnd = Map(checked(clause.HandlerOffset + clause.HandlerLength));
            if (tryStart >= tryEnd || handlerStart >= handlerEnd || tryEnd > codeSize || handlerEnd > codeSize)
                return Failure("An EH source range has no exact non-empty final native-PC range.");
            clauses.Add(new(clause.Kind, tryStart, tryEnd - tryStart, handlerStart, handlerEnd - handlerStart,
                clause.CatchTypeId, clause.Ordinal));
        }
        ManagedEhNativeOperationV1[] operations = plan.Operations.Select(operation => new ManagedEhNativeOperationV1(
                operation.Kind, Map(operation.IlOffset), operation.LeaveTarget < 0 ? -1 : Map(operation.LeaveTarget),
                operation.RuntimeHelperSymbol)).OrderBy(static row => row.NativeOffsetBytes).ToArray();
        byte[] ehInfo = EncodeEh(clauses);

        ManagedEhNativeFinallyContinuationPlanV1? finallyContinuations = null;
        if (plan.Clauses.Any(static clause => clause.Kind == HybridCpuManagedEhClauseKindV1.Finally))
        {
            HybridCpuFrameSlotV2? tokenSlot = allocation.Witness.Frame.Slots.SingleOrDefault(static slot =>
                slot.Identity == ManagedEhFrameHomesV1.FinallyContinuationTokenSlot);
            if (tokenSlot is null || tokenSlot.SizeBytes != 8 || tokenSlot.AlignmentBytes < 8)
                return Failure("Managed finally requires one exact aligned frame-owned continuation-token slot.");
            ManagedEhNativeFinallyContinuationV1[] nativeContinuations = plan.FinallyContinuations.Continuations
                .Select(continuation => new ManagedEhNativeFinallyContinuationV1(
                    continuation.Token, Map(continuation.LeaveOffset), Map(continuation.TargetOffset),
                    continuation.Steps.Select(step =>
                    {
                        ManagedEhClausePlanV1 clause = plan.Clauses.Single(row => row.Ordinal == step.ClauseOrdinal);
                        return new ManagedEhNativeFinallyContinuationStepV1(
                            step.ClauseOrdinal, Map(step.HandlerOffset), Map(clause.HandlerOffset + clause.HandlerLength),
                            step.ReleaseCatchScopesBeforeEntry, step.NextClauseOrdinal, Map(step.NextOffset));
                    }).ToArray(),
                    continuation.ReleaseCatchScopesBeforeTarget)).ToArray();
            byte[] encoding = EncodeFinallyContinuations(tokenSlot.OffsetFromAdjustedStackPointerBytes, nativeContinuations);
            finallyContinuations = new(tokenSlot.OffsetFromAdjustedStackPointerBytes, nativeContinuations, encoding,
                Hash(string.Join('|', "hybridcpu.managed-finally-native/v1", plan.FinallyContinuations.Digest,
                    allocation.Witness.WitnessDigest, Convert.ToHexString(encoding))));
        }

        HybridCpuManagedUnwindRecordV2 unwind = CreateFrameUnwind(allocation.Witness.Frame);
        byte[] unwindInfo = HybridCpuManagedUnwindCodecV2.Encode(unwind);
        List<HybridCpuObjectSectionV1> sections = new List<HybridCpuObjectSectionV1>
        {
            new(".hceh", HybridCpuObjectSectionKind.ExceptionHandling, 8, ehInfo, (ulong)ehInfo.Length),
            new(".hcunwind", HybridCpuObjectSectionKind.Unwind, 8, unwindInfo, (ulong)unwindInfo.Length)
        };
        if (finallyContinuations is not null)
            sections.Add(new(".hcfinally", HybridCpuObjectSectionKind.ReadOnlyData, 8,
                finallyContinuations.Encoding, (ulong)finallyContinuations.Encoding.Length));
        string digest = Hash(string.Join('|', HybridCpuManagedEhSchemaV1.SchemaId, plan.ContractDigest,
            allocation.Witness.WitnessDigest, Convert.ToHexString(ehInfo), Convert.ToHexString(unwindInfo),
            finallyContinuations?.Digest ?? "no-finally-continuation"));
        cancellationToken.ThrowIfCancellationRequested();
        return new(HybridCpuManagedMetadataStatusV1.Finalized,
            "EH clauses and unwind data were derived from final native placement and frame layout.",
            clauses, operations, ehInfo, unwindInfo, unwind, sections, digest, finallyContinuations);
    }

    // Shared by EH and handler-free managed frames. Offsets are relative to the
    // caller CFA, not the adjusted stack pointer used by allocation frame slots.
    internal static HybridCpuManagedUnwindRecordV2 CreateFrameUnwind(HybridCpuFrameLayoutV2 frame)
    {
        HybridCpuFrameSlotV2? returnSlot = frame.Slots.SingleOrDefault(static row => row.Identity == "saved:x1");
        HybridCpuManagedSavedRegisterV1[] saved = frame.Slots.Where(static row => row.Identity.StartsWith("saved:x", StringComparison.Ordinal))
            .Select(row => new HybridCpuManagedSavedRegisterV1(
                int.Parse(row.Identity.AsSpan("saved:x".Length), CultureInfo.InvariantCulture),
                checked(row.OffsetFromAdjustedStackPointerBytes - frame.FrameSizeBytes)))
            .OrderBy(static row => row.RegisterId).ToArray();
        return new HybridCpuManagedUnwindRecordV2(HybridCpuManagedFrameKindV1.Managed,
            HybridCpuManagedCfaBaseV1.StackPointer, frame.FrameSizeBytes,
            returnSlot is null ? HybridCpuNativeAbiContractV2.ReturnAddressRegister : null,
            returnSlot is null ? null : checked(returnSlot.OffsetFromAdjustedStackPointerBytes - frame.FrameSizeBytes), saved);
    }

    private static byte[] EncodeEh(IReadOnlyList<HybridCpuManagedEhClauseRegistrationV1> clauses)
    {
        if (clauses.Count > HybridCpuManagedEhSchemaV1.MaximumClausesPerMethod)
            throw new ArgumentException("EH clause budget exceeded.", nameof(clauses));
        byte[] bytes = new byte[HybridCpuManagedEhClauseEncodingV1.HeaderSizeBytes +
            clauses.Count * HybridCpuManagedEhClauseEncodingV1.ClauseSizeBytes];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, HybridCpuManagedEhSchemaV1.EhMagic);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), HybridCpuManagedEhSchemaV1.SchemaVersion);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(HybridCpuManagedEhClauseEncodingV1.CountOffset), clauses.Count);
        for (int index = 0, offset = HybridCpuManagedEhClauseEncodingV1.HeaderSizeBytes; index < clauses.Count;
             index++, offset += HybridCpuManagedEhClauseEncodingV1.ClauseSizeBytes)
        {
            HybridCpuManagedEhClauseRegistrationV1 row = clauses[index];
            bytes[offset + HybridCpuManagedEhClauseEncodingV1.KindOffset] = (byte)row.Kind;
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset + HybridCpuManagedEhClauseEncodingV1.TryStartOffset), row.TryStartOffsetBytes);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset + HybridCpuManagedEhClauseEncodingV1.TrySizeOffset), row.TrySizeBytes);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset + HybridCpuManagedEhClauseEncodingV1.HandlerStartOffset), row.HandlerStartOffsetBytes);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset + HybridCpuManagedEhClauseEncodingV1.HandlerSizeOffset), row.HandlerSizeBytes);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(offset + HybridCpuManagedEhClauseEncodingV1.CatchTypeIdOffset), row.CatchTypeId);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset + HybridCpuManagedEhClauseEncodingV1.OrdinalOffset), row.Ordinal);
        }
        return bytes;
    }

    private static byte[] EncodeFinallyContinuations(int tokenSlotOffset,
        IReadOnlyList<ManagedEhNativeFinallyContinuationV1> continuations)
    {
        int stepCount = continuations.Sum(static continuation => continuation.Steps.Count);
        if (continuations.Count > HybridCpuManagedEhSchemaV1.MaximumOperationsPerMethod ||
            stepCount > HybridCpuManagedEhSchemaV1.MaximumOperationsPerMethod * HybridCpuManagedEhSchemaV1.MaximumClausesPerMethod)
            throw new ArgumentException("Managed finally continuation metadata exceeds its deterministic budget.", nameof(continuations));
        int continuationBytes = checked(continuations.Count * HybridCpuManagedFinallyContinuationEncodingV1.ContinuationRowSizeBytes);
        byte[] bytes = new byte[checked(HybridCpuManagedFinallyContinuationEncodingV1.HeaderSizeBytes + continuationBytes +
            stepCount * HybridCpuManagedFinallyContinuationEncodingV1.StepRowSizeBytes)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, HybridCpuManagedFinallyContinuationEncodingV1.Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), HybridCpuManagedFinallyContinuationEncodingV1.Version);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(HybridCpuManagedFinallyContinuationEncodingV1.ContinuationCountOffset), continuations.Count);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(HybridCpuManagedFinallyContinuationEncodingV1.StepCountOffset), stepCount);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(HybridCpuManagedFinallyContinuationEncodingV1.TokenSlotOffset), tokenSlotOffset);
        int stepIndex = 0;
        for (int index = 0; index < continuations.Count; index++)
        {
            ManagedEhNativeFinallyContinuationV1 continuation = continuations[index];
            int offset = HybridCpuManagedFinallyContinuationEncodingV1.HeaderSizeBytes +
                index * HybridCpuManagedFinallyContinuationEncodingV1.ContinuationRowSizeBytes;
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset + HybridCpuManagedFinallyContinuationEncodingV1.TokenOffset), continuation.Token);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset + HybridCpuManagedFinallyContinuationEncodingV1.LeaveOffset), continuation.LeaveOffsetBytes);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset + HybridCpuManagedFinallyContinuationEncodingV1.TargetOffset), continuation.TargetOffsetBytes);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset + HybridCpuManagedFinallyContinuationEncodingV1.FirstStepOffset), stepIndex);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset + HybridCpuManagedFinallyContinuationEncodingV1.ContinuationStepCountOffset), continuation.Steps.Count);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset + HybridCpuManagedFinallyContinuationEncodingV1.ReleaseBeforeTargetOffset), continuation.ReleaseCatchScopesBeforeTarget);
            foreach (ManagedEhNativeFinallyContinuationStepV1 step in continuation.Steps)
            {
                int stepOffset = HybridCpuManagedFinallyContinuationEncodingV1.HeaderSizeBytes + continuationBytes +
                    stepIndex++ * HybridCpuManagedFinallyContinuationEncodingV1.StepRowSizeBytes;
                BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(stepOffset + HybridCpuManagedFinallyContinuationEncodingV1.StepClauseOrdinalOffset), step.ClauseOrdinal);
                BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(stepOffset + HybridCpuManagedFinallyContinuationEncodingV1.StepHandlerOffset), step.HandlerOffsetBytes);
                BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(stepOffset + HybridCpuManagedFinallyContinuationEncodingV1.StepReleaseBeforeEntryOffset), step.ReleaseCatchScopesBeforeEntry);
                BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(stepOffset + HybridCpuManagedFinallyContinuationEncodingV1.StepNextClauseOrdinalOffset), step.NextClauseOrdinal ?? -1);
                BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(stepOffset + HybridCpuManagedFinallyContinuationEncodingV1.StepNextOffset), step.NextOffsetBytes);
                BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(stepOffset + HybridCpuManagedFinallyContinuationEncodingV1.StepHandlerEndOffset), step.HandlerEndOffsetBytes);
            }
        }
        return bytes;
    }

    private static bool ValidPlan(ManagedEhMethodPlanV1 plan)
    {
        if (string.IsNullOrWhiteSpace(plan.MethodIdentity) || string.IsNullOrWhiteSpace(plan.InstructionIdentityPrefix) ||
            plan.MethodBodySize <= 0 || plan.Clauses is null || plan.Operations is null ||
            plan.Clauses.Count is 0 or > HybridCpuManagedEhSchemaV1.MaximumClausesPerMethod ||
            plan.Operations.Count > HybridCpuManagedEhSchemaV1.MaximumOperationsPerMethod ||
            !string.Equals(plan.ContractDigest, ManagedEhPlanContractV1.ComputeDigest(plan.MethodIdentity,
                plan.InstructionIdentityPrefix, plan.MethodBodySize, plan.Clauses, plan.Operations),
                StringComparison.Ordinal) ||
            !plan.Clauses.Select(static row => row.Ordinal).SequenceEqual(Enumerable.Range(0, plan.Clauses.Count)))
            return false;
        foreach (ManagedEhClausePlanV1 row in plan.Clauses)
            if (!Enum.IsDefined(row.Kind) || row.TryOffset < 0 || row.TryLength <= 0 || row.HandlerOffset < 0 ||
                row.HandlerLength <= 0 || row.TryOffset > plan.MethodBodySize - row.TryLength ||
                row.HandlerOffset > plan.MethodBodySize - row.HandlerLength ||
                row.Kind == HybridCpuManagedEhClauseKindV1.Catch &&
                    (row.CatchTypeId == 0 || string.IsNullOrWhiteSpace(row.CatchTypeIdentity)) ||
                row.Kind == HybridCpuManagedEhClauseKindV1.Finally &&
                    (row.CatchTypeId != 0 || row.CatchTypeIdentity is not null))
                return false;
        foreach (ManagedEhOperationPlanV1 row in plan.Operations)
        {
            if (!Enum.IsDefined(row.Kind) || row.IlOffset < 0 || row.IlOffset >= plan.MethodBodySize)
                return false;
            if (row.Kind == ManagedEhOperationKindV1.Leave)
            {
                if (row.LeaveTarget < 0 || row.LeaveTarget > plan.MethodBodySize || row.RuntimeHelperSymbol.Length != 0)
                    return false;
            }
            else
            {
                if (row.LeaveTarget != -1) return false;
                bool helperValid = row.Kind switch
                {
                    ManagedEhOperationKindV1.Throw => row.RuntimeHelperSymbol == "__hybridcpu_managed_throw",
                    ManagedEhOperationKindV1.Rethrow => row.RuntimeHelperSymbol == "__hybridcpu_managed_rethrow",
                    ManagedEhOperationKindV1.EndFinally => row.RuntimeHelperSymbol == "__hybridcpu_managed_endfinally",
                    _ => row.RuntimeHelperSymbol.Length == 0
                };
                if (!helperValid) return false;
            }
        }
        return true;
    }

    private static SortedDictionary<int, int> FinalIlPlacement(CancellationToken cancellationToken,
        IrProgramBundlingResult bundles, string prefix)
    {
        SortedDictionary<int, int> result = new SortedDictionary<int, int>();
        int bundleIndex = 0;
        foreach (IrBasicBlockBundlingResult block in bundles.BlockResults.OrderBy(static row => row.Block.StartInstructionIndex))
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (IrMaterializedBundle bundle in block.Bundles.OrderBy(static row => row.Cycle))
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (IrMaterializedBundleSlot slot in bundle.Slots.Where(static row => row.Instruction is not null)
                             .OrderBy(static row => row.SlotIndex))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string identity = slot.Instruction!.StableIdentity;
                    // Native dispatch/continuation must enter before frame-home reloads and catch ABI copies.
                    if (!identity.StartsWith(prefix, StringComparison.Ordinal) &&
                        !identity.StartsWith("eh-home:", StringComparison.Ordinal) &&
                        !identity.StartsWith("eh:handler:il_", StringComparison.Ordinal)) continue;
                    int marker = identity.LastIndexOf(":il_", StringComparison.Ordinal);
                    if (marker >= 0 && marker + 8 <= identity.Length &&
                        int.TryParse(identity.AsSpan(marker + 4, 4), NumberStyles.HexNumber,
                            CultureInfo.InvariantCulture, out int ilOffset))
                        result.TryAdd(ilOffset, checked(bundleIndex * HybridCpuBundleSerializer.BundleSizeBytes));
                }
                bundleIndex++;
            }
        }
        return result;
    }

    private static ManagedEhFinalizationArtifactV1 Failure(string reason) =>
        new(HybridCpuManagedMetadataStatusV1.Unknown, reason, [], [], [], [], null, [],
            Hash($"{HybridCpuManagedEhSchemaV1.SchemaId}|failure|{reason}"));

    private static string Hash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
}
